#!/usr/bin/env node
'use strict';
[process.stdin, process.stdout, process.stderr].forEach(s => { if (s && typeof s.on === 'function') s.on('error', () => {}); });

const net = require('node:net');
const http = require('node:http');
const https = require('node:https');
const zlib = require('node:zlib');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { URL } = require('node:url');
const { McpServerStateCache, safeError } = require('./mcp-state-cache');
const { McpPollManager } = require('./mcp-poll-manager');

const MAX_BODY_BYTES = 64 * 1024 * 1024;
const MANAGER_API_KEY = String(process.env.MCPBRIDGE_MANAGER_API_KEY || '');
const PROTOCOL_VERSION = '2025-06-18';
const MODERN_PROTOCOL_VERSION = '2026-07-28';
const MODERN_CLIENT_INFO = { name: 'MCP Bridge Router', version: '0.9.1' };
const directHttpAgent = new http.Agent({ keepAlive: true, keepAliveMsecs: 15000, timeout: 65000, maxSockets: 32 });
const directHttpsAgent = new https.Agent({ keepAlive: true, keepAliveMsecs: 15000, timeout: 65000, maxSockets: 32 });
const pinnedRouteByServer = new Map();

let proxyEnvironmentConfigured = Boolean(
  process.env.HTTPS_PROXY || process.env.https_proxy ||
  process.env.HTTP_PROXY || process.env.http_proxy
);
let proxyConfigurationError = null;
let currentProxySignature = '';
let restoreGlobalProxy = null;

async function restartRemoteHttpWorkers() {
  pinnedRouteByServer.clear();
}

function configureEnvironmentProxy(customEnv = null) {
  const envSource = customEnv || process.env;
  const hasProxy = Boolean(
    envSource.HTTPS_PROXY || envSource.https_proxy ||
    envSource.HTTP_PROXY || envSource.http_proxy
  );
  proxyEnvironmentConfigured = hasProxy;
  if (!hasProxy) {
    const hadProxy = Boolean(currentProxySignature || restoreGlobalProxy);
    if (typeof restoreGlobalProxy === 'function') { try { restoreGlobalProxy(); } catch (_) {} restoreGlobalProxy = null; }
    currentProxySignature = '';
    proxyConfigurationError = null;
    return hadProxy;
  }
  if (typeof http.setGlobalProxyFromEnv !== 'function') {
    proxyConfigurationError = 'This Node.js runtime does not support built-in environment proxy handling.';
    return false;
  }

  const noProxyEntries = [
    envSource.NO_PROXY || '',
    envSource.no_proxy || '',
    '127.0.0.1',
    'localhost',
    '::1'
  ].flatMap(value => String(value).split(','))
    .map(value => value.trim())
    .filter(Boolean);
  const noProxy = [...new Set(noProxyEntries)].join(',');
  const sig = `${envSource.HTTPS_PROXY || ''}|${envSource.HTTP_PROXY || ''}|${noProxy}`;
  if (sig === currentProxySignature) return false;

  try {
    if (typeof restoreGlobalProxy === 'function') { try { restoreGlobalProxy(); } catch (_) {} restoreGlobalProxy = null; }
    restoreGlobalProxy = http.setGlobalProxyFromEnv({
      ...process.env,
      ...envSource,
      NO_PROXY: noProxy,
      no_proxy: noProxy
    });
    currentProxySignature = sig;
    proxyConfigurationError = null;
    return true;
  } catch (error) {
    proxyConfigurationError = error instanceof Error ? error.message : String(error);
    proxyConfigurationError = proxyConfigurationError.replace(
      /(https?:\/\/)[^/@\s]+@/gi,
      '$1[credentials]@'
    );
    return false;
  }
}

function applyDynamicProxyConfig(proxyEnv) {
  if (!proxyEnv || typeof proxyEnv !== 'object') return false;
  for (const key of ['HTTP_PROXY', 'http_proxy', 'HTTPS_PROXY', 'https_proxy', 'NO_PROXY', 'no_proxy']) {
    if (typeof proxyEnv[key] === 'string' && proxyEnv[key]) {
      process.env[key] = proxyEnv[key];
    } else {
      delete process.env[key];
    }
  }
  return configureEnvironmentProxy(process.env);
}

configureEnvironmentProxy();

function parseArgs(argv) {
  const args = { listen: '127.0.0.1', port: 8001, config: null };
  for (let i = 2; i < argv.length; i++) {
    const key = argv[i];
    const val = argv[i + 1];
    if (key === '--listen' && val) { args.listen = val; i++; }
    else if (key === '--port' && val) { args.port = Number(val); i++; }
    else if (key === '--config' && val) { args.config = val; i++; }
  }
  return args;
}

const options = parseArgs(process.argv);
let upstreams = [];
const upstreamSessions = new Map();
const upstreamInitialized = new Set();
const upstreamModernUnsupported = new Set();
const upstreamInitializationPromises = new Map();
let nextLegacySseRpcId = 1000000000;
let nextUpstreamRpcId = 2000000000;
const upstreamTransportModes = new Map();
const legacySseConnections = new Map();
const upstreamRequests = new Map();
const upstreamRequestEpochs = new Map();
const upstreamSessionEpochs = new Map();
const lastUpstreamFailureByServer = new Map();
const toolRouteMap = new Map();

const stdioToolsCachePath = options.config
  ? path.join(path.dirname(options.config), 'stdio-tools-cache.json')
  : null;
const stdioKnownToolsByServer = new Map();
if (stdioToolsCachePath && fs.existsSync(stdioToolsCachePath)) {
  try {
    const rawCache = JSON.parse(fs.readFileSync(stdioToolsCachePath, 'utf8'));
    if (rawCache && typeof rawCache === 'object') {
      for (const [k, v] of Object.entries(rawCache)) {
        if (Array.isArray(v) && v.length > 0) stdioKnownToolsByServer.set(k, v);
      }
    }
  } catch (_) {}
}

function saveStdioToolsCache() {
  if (!stdioToolsCachePath) return;
  try {
    const obj = {};
    for (const [k, v] of stdioKnownToolsByServer.entries()) obj[k] = v;
    const tmpPath = stdioToolsCachePath + '.tmp';
    fs.writeFileSync(tmpPath, JSON.stringify(obj), 'utf8');
    fs.renameSync(tmpPath, stdioToolsCachePath);
  } catch (_) {}
}

const stdioRevalidationInFlight = new Set();
const stdioLastRevalidatedAt = new Map();
const STDIO_REVALIDATE_INTERVAL_MS = 15 * 60 * 1000;

function revalidateStdioServerInBackground(upstream, isCurrent) {
  if (!upstream || upstream.transport !== 'stdio') return;
  const serverId = String(upstream.id);
  if (stdioRevalidationInFlight.has(serverId)) return;

  const lastTime = stdioLastRevalidatedAt.get(serverId) || 0;
  if (Date.now() - lastTime < STDIO_REVALIDATE_INTERVAL_MS) return;

  stdioRevalidationInFlight.add(serverId);
  stdioLastRevalidatedAt.set(serverId, Date.now());

  setTimeout(async () => {
    try {
      if (typeof isCurrent === 'function' && !isCurrent()) return;
      const res = await callUpstreamWithAutoInit(
        upstream,
        { jsonrpc: '2.0', id: ++nextUpstreamRpcId, method: 'tools/list', params: {} },
        15000,
        null,
        isCurrent
      );
      const freshTools = res && res.data && res.data.result && res.data.result.tools;
      if (res && res.ok && Array.isArray(freshTools) && freshTools.length > 0) {
        const prevJson = JSON.stringify(stdioKnownToolsByServer.get(serverId) || []);
        const nextJson = JSON.stringify(freshTools);
        stdioKnownToolsByServer.set(serverId, freshTools);
        saveStdioToolsCache();

        if (prevJson !== nextJson) {
          const currentState = stateCache.getInternalServer(serverId);
          if (currentState && currentState.health === 'online') {
            stateCache.setSuccess(serverId, {
              tools: freshTools,
              serverInfo: currentState.serverInfo,
              completedAt: new Date().toISOString(),
              responseTimeMs: currentState.responseTimeMs,
              protocolMode: currentState.protocolMode || 'streamable-http'
            });
            rebuildToolRouteMap();
          }
        }
      }
    } catch (_) {
    } finally {
      stdioRevalidationInFlight.delete(serverId);
    }
  }, 500);
}

function isManagerRequestAuthorized(request) {
  if (!MANAGER_API_KEY) return false;
  const provided = String(request.headers['x-mcpbridge-manager-key'] || '');
  const expectedBuffer = Buffer.from(MANAGER_API_KEY, 'utf8');
  const providedBuffer = Buffer.from(provided, 'utf8');
  return expectedBuffer.length === providedBuffer.length &&
    crypto.timingSafeEqual(expectedBuffer, providedBuffer);
}

function loadConfigFile(filePath) {
  if (!filePath || !fs.existsSync(filePath)) return;
  try {
    const raw = fs.readFileSync(filePath, 'utf8');
    const parsed = JSON.parse(raw);
    if (Array.isArray(parsed.upstreams)) {
      upstreams = parsed.upstreams.filter(u =>
        u && u.id && (u.enabled === false || u.pending === true ||
          (typeof u.url === 'string' && u.url.trim()))
      );
    }
  } catch (_) {}
}

loadConfigFile(options.config);

function sanitizePrefix(id) {
  return String(id || 'srv').replace(/[^a-zA-Z0-9_-]/g, '_');
}

function stripRootsCapability(jsonObj) {
  if (jsonObj && jsonObj.method === 'initialize' && jsonObj.params && jsonObj.params.capabilities) {
    delete jsonObj.params.capabilities.roots;
  }
  return jsonObj;
}

function parseSseOrJson(text) {
  if (!text) return null;
  const trimmed = text.trim();
  if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
    try { return JSON.parse(trimmed); } catch (_) {}
  }
  const blocks = trimmed.split(/(?:\r?\n){2,}/);
  let lastParsed = null;
  for (const block of blocks) {
    const dataLines = [];
    for (const line of block.split(/\r?\n/)) {
      if (line.toLowerCase().startsWith('data:')) {
        dataLines.push(line.slice(5).trimStart());
      }
    }
    if (dataLines.length > 0) {
      const candidate = dataLines.join('\n').trim();
      if (candidate) {
        try { lastParsed = JSON.parse(candidate); } catch (_) {}
      }
    }
  }
  return lastParsed;
}

function getHttpTransport(targetUrl) {
  if (targetUrl.protocol === 'https:') return https;
  if (targetUrl.protocol === 'http:') return http;
  throw new Error('MCP HTTP URL must use http:// or https://.');
}

function createUpstreamHeaders(upstream, defaults = {}) {
  const headers = {
    'User-Agent': 'MCP-Bridge/0.9.1'
  };
  const customHeaders = upstream.headers && typeof upstream.headers === 'object' && !Array.isArray(upstream.headers)
    ? upstream.headers
    : {};
  const reservedHeaders = new Set([
    'host', 'content-length', 'content-type', 'accept',
    'connection', 'transfer-encoding', 'mcp-session-id'
  ]);
  for (const [name, value] of Object.entries(customHeaders)) {
    const normalizedName = String(name || '').trim();
    if (!normalizedName || reservedHeaders.has(normalizedName.toLowerCase()) ||
        /[\r\n:]/.test(normalizedName) || /[\r\n]/.test(String(value ?? ''))) continue;
    headers[normalizedName] = String(value ?? '');
  }
  return { ...headers, ...defaults };
}

function encodeMcpHeaderValue(value) {
  const text = String(value);
  const safeAscii = /^[\x20-\x7E\t]*$/.test(text) &&
    !/^\s|\s$/.test(text) &&
    !(text.startsWith('=?base64?') && text.endsWith('?='));
  return safeAscii
    ? text
    : '=?base64?' + Buffer.from(text, 'utf8').toString('base64') + '?=';
}

function createModernRequestPayload(rpcPayload) {
  const params = rpcPayload.params && typeof rpcPayload.params === 'object' && !Array.isArray(rpcPayload.params)
    ? { ...rpcPayload.params }
    : {};
  params._meta = {
    ...(params._meta && typeof params._meta === 'object' && !Array.isArray(params._meta) ? params._meta : {}),
    'io.modelcontextprotocol/protocolVersion': MODERN_PROTOCOL_VERSION,
    'io.modelcontextprotocol/clientInfo': MODERN_CLIENT_INFO,
    'io.modelcontextprotocol/clientCapabilities': {}
  };
  return { ...rpcPayload, params };
}

function sendStreamableHttpRpcSingle(upstream, rpcPayload, stallTimeoutMs = 15000, modern = false, extraHeaders = null, legacyProtocolVersion = null, routeMode = 'direct', onReqCreated = null, shouldAbort = null) {
  const expectedSessionEpoch = getUpstreamSessionEpoch(upstream.id);
  return new Promise((resolve) => {
    let targetUrl;
    try { targetUrl = new URL(upstream.url); }
    catch (err) { return resolve({ ok: false, error: err.message }); }

    let transport;
    try { transport = getHttpTransport(targetUrl); }
    catch (err) { return resolve({ ok: false, error: err.message }); }

    const outboundPayload = modern ? createModernRequestPayload(rpcPayload) : rpcPayload;
    const bodyBuf = Buffer.from(JSON.stringify(outboundPayload), 'utf8');
    const headers = createUpstreamHeaders(upstream, {
      'Content-Type': 'application/json',
      'Accept': 'application/json, text/event-stream',
      'Accept-Encoding': 'br, gzip, deflate',
      'Connection': 'keep-alive',
      'Content-Length': bodyBuf.length
    });

    if (modern) {
      headers['MCP-Protocol-Version'] = MODERN_PROTOCOL_VERSION;
      headers['Mcp-Method'] = outboundPayload.method;
      const methodParams = outboundPayload.params || {};
      const headerNameValue = methodParams.name || methodParams.uri;
      if (headerNameValue !== undefined && headerNameValue !== null &&
          ['tools/call', 'resources/read', 'prompts/get'].includes(outboundPayload.method))
        headers['Mcp-Name'] = encodeMcpHeaderValue(headerNameValue);

      if (extraHeaders && typeof extraHeaders === 'object') {
        for (const [name, value] of Object.entries(extraHeaders)) {
          if (name && !/[\r\n:]/.test(name) && !/[\r\n]/.test(String(value ?? '')))
            headers[name] = String(value);
        }
      }
    } else {
      headers['MCP-Protocol-Version'] = legacyProtocolVersion || PROTOCOL_VERSION;
      const existingSession = upstreamSessions.get(upstream.id);
      if (existingSession) headers['Mcp-Session-Id'] = existingSession;
    }

    let resolved = false;
    let stallTimer = null;
    let req = null;

    const clearWatchdog = () => {
      if (stallTimer) {
        clearTimeout(stallTimer);
        stallTimer = null;
      }
    };

    const markActivity = () => {
      if (resolved) return;
      clearWatchdog();
      const silenceLimit = Math.max(4000, Number(stallTimeoutMs) || 15000);
      stallTimer = setTimeout(() => {
        if (!resolved && req) {
          req.destroy(new Error('Upstream socket stalled (no data activity).'));
        }
      }, silenceLimit);
    };

    markActivity();

    const reqOptions = {
      hostname: targetUrl.hostname,
      port: targetUrl.port || (targetUrl.protocol === 'https:' ? 443 : 80),
      path: targetUrl.pathname + targetUrl.search,
      method: 'POST',
      headers
    };
    if (routeMode === 'direct') {
      reqOptions.agent = targetUrl.protocol === 'https:' ? directHttpsAgent : directHttpAgent;
    }

    req = transport.request(reqOptions, (res) => {
      if (typeof shouldAbort === 'function' && shouldAbort()) {
        clearWatchdog();
        try { res.destroy(); } catch (_) {}
        try { req.destroy(); } catch (_) {}
        if (!resolved) {
          resolved = true;
          resolve({ ok: false, status: 0, error: 'Route check superseded by winning route.' });
        }
        return;
      }
      markActivity();
      const newSession = res.headers['mcp-session-id'];
      if (!modern && newSession && isUpstreamSessionEpochCurrent(upstream.id, expectedSessionEpoch) &&
          !(typeof shouldAbort === 'function' && shouldAbort()))
        upstreamSessions.set(String(upstream.id), String(newSession));

      const isSse = String(res.headers['content-type'] || '').toLowerCase().includes('text/event-stream');
      let chunks = '';
      const finish = (parsed) => {
        if (resolved) return;
        resolved = true;
        clearWatchdog();
        const status = res.statusCode || 0;
        const isOk = status >= 200 && status < 300 && (!!parsed || outboundPayload.id === undefined);
        if (!isOk && res.socket) {
          try { res.socket.destroy(); } catch (_) {}
        }
        resolve({
          ok: isOk,
          status,
          data: parsed,
          error: status >= 200 && status < 300 ? null : (chunks || ('Upstream HTTP ' + status + '.'))
        });
      };

      if (outboundPayload.id === undefined && res.statusCode >= 200 && res.statusCode < 300) {
        finish(null);
        res.resume();
        return;
      }

      res.on('data', () => markActivity());

      const contentEncoding = String(res.headers['content-encoding'] || '').toLowerCase().trim();
      let stream = res;
      if (contentEncoding === 'br') {
        stream = res.pipe(zlib.createBrotliDecompress());
      } else if (contentEncoding === 'gzip' || contentEncoding === 'x-gzip') {
        stream = res.pipe(zlib.createGunzip());
      } else if (contentEncoding === 'deflate') {
        stream = res.pipe(zlib.createInflate());
      }

      stream.setEncoding('utf8');
      stream.on('data', (chunk) => {
        markActivity();
        chunks += chunk;
        if (isSse && (chunk.includes('\n\n') || chunk.includes('\r\n\r\n') || chunks.endsWith('\n\n') || chunks.endsWith('\r\n\r\n'))) {
          const parsed = parseSseOrJson(chunks);
          if (parsed && (parsed.result !== undefined || parsed.error !== undefined)) {
            finish(parsed);
            res.resume();
          }
        }
      });
      stream.on('end', () => finish(parseSseOrJson(chunks)));
      stream.on('error', (err) => {
        clearWatchdog();
        if (res.socket) { try { res.socket.destroy(); } catch (_) {} }
        if (!resolved) {
          resolved = true;
          resolve({ ok: false, status: res.statusCode || 0, error: err.message });
        }
      });
    });

    if (typeof onReqCreated === 'function') onReqCreated(req);

    req.on('socket', (sock) => {
      try { sock.setMaxListeners(0); } catch (_) {}
      markActivity();
      if (!sock.connecting) return;
      const onSockActivity = () => markActivity();
      sock.on('lookup', onSockActivity);
      sock.on('connect', onSockActivity);
      sock.on('secureConnect', onSockActivity);
      req.once('close', () => {
        sock.removeListener('lookup', onSockActivity);
        sock.removeListener('connect', onSockActivity);
        sock.removeListener('secureConnect', onSockActivity);
      });
    });

    trackUpstreamRequest(upstream.id, req);
    req.on('error', (err) => {
      clearWatchdog();
      if (!resolved) {
        resolved = true;
        resolve({ ok: false, status: 0, error: err.message });
      }
    });
    req.end(bodyBuf);
  });
}

async function sendStreamableHttpRpc(upstream, rpcPayload, stallTimeoutMs = 15000, modern = false, extraHeaders = null, legacyProtocolVersion = null) {
  const key = String(upstream.id);
  const isRemoteHttp = upstream.scope === 'remote' && upstream.transport !== 'stdio';
  if (!isRemoteHttp || !proxyEnvironmentConfigured) {
    return sendStreamableHttpRpcSingle(upstream, rpcPayload, stallTimeoutMs, modern, extraHeaders, legacyProtocolVersion, 'direct');
  }

  const pinned = pinnedRouteByServer.get(key);
  if (pinned) {
    const res = await sendStreamableHttpRpcSingle(upstream, rpcPayload, stallTimeoutMs, modern, extraHeaders, legacyProtocolVersion, pinned);
    if (res.ok && !(res.data && res.data.error)) {
      return res;
    }
    pinnedRouteByServer.delete(key);
  }

  const routeModes = ['direct', 'proxy'];
  const activeReqs = [];
  let lastFail = null;
  let routeSettled = false;

  const racePromises = routeModes.map(mode =>
    sendStreamableHttpRpcSingle(
      upstream, rpcPayload, stallTimeoutMs, modern, extraHeaders, legacyProtocolVersion, mode,
      req => {
        if (routeSettled) {
          try { req.destroy(); } catch (_) {}
          return;
        }
        activeReqs.push({ mode, req });
      },
      () => routeSettled
    ).then(res => {
      if (!routeSettled && res.ok && !(res.data && res.data.error)) {
        routeSettled = true;
        pinnedRouteByServer.set(key, mode);
        for (const item of activeReqs) {
          if (item.mode !== mode) {
            try { item.req.destroy(); } catch (_) {}
          }
        }
        return { mode, res };
      }
      lastFail = res;
      throw res;
    })
  );

  try {
    const winner = await Promise.any(racePromises);
    return winner.res;
  } catch (_) {
    return lastFail || { ok: false, status: 0, error: 'All transport routes failed.' };
  }
}
function getUpstreamRequestEpoch(upstreamId) {
  return upstreamRequestEpochs.get(String(upstreamId)) || 0;
}

function isUpstreamRequestEpochCurrent(upstreamId, expectedEpoch) {
  return getUpstreamRequestEpoch(upstreamId) === expectedEpoch;
}

function getUpstreamSessionEpoch(upstreamId) {
  return upstreamSessionEpochs.get(String(upstreamId)) || 0;
}

function isUpstreamSessionEpochCurrent(upstreamId, expectedEpoch) {
  return getUpstreamSessionEpoch(upstreamId) === expectedEpoch;
}

function trackUpstreamRequest(upstreamId, request) {
  const key = String(upstreamId);
  let requests = upstreamRequests.get(key);
  if (!requests) {
    requests = new Set();
    upstreamRequests.set(key, requests);
  }
  requests.add(request);
  request.__mcpBridgeServerId = key;
  request.once('close', () => {
    const ownerId = String(request.__mcpBridgeServerId || key);
    const current = upstreamRequests.get(ownerId);
    if (!current) return;
    current.delete(request);
    if (current.size === 0) upstreamRequests.delete(ownerId);
  });
}

function abortUpstreamRequests(upstreamId, reason = 'MCP worker configuration changed.') {
  const key = String(upstreamId);
  upstreamRequestEpochs.set(key, getUpstreamRequestEpoch(key) + 1);
  upstreamSessionEpochs.set(key, getUpstreamSessionEpoch(key) + 1);
  const requests = upstreamRequests.get(key);
  if (requests) {
    upstreamRequests.delete(key);
    for (const request of requests) {
      try { request.destroy(new Error(reason)); } catch (_) {}
    }
  }
  closeLegacySseConnection(key, reason);
}

function closeLegacySseConnection(upstreamId, reason = 'Legacy SSE connection closed.', expectedConnection = null) {
  const connection = legacySseConnections.get(String(upstreamId));
  if (!connection || (expectedConnection && connection !== expectedConnection)) return;
  const key = String(upstreamId);
  if (isUpstreamSessionEpochCurrent(key, connection.sessionEpoch))
    upstreamSessionEpochs.set(key, getUpstreamSessionEpoch(key) + 1);
  if (connection.readyTimer) clearTimeout(connection.readyTimer);
  legacySseConnections.delete(key);
  upstreamInitialized.delete(String(upstreamId));
  upstreamSessions.delete(String(upstreamId));
  upstreamTransportModes.delete(String(upstreamId));
  connection.closed = true;
  const error = new Error(reason);
  if (!connection.ready) {
    connection.ready = true;
    connection.rejectReady(error);
  }
  for (const [id, pending] of connection.pending) {
    clearTimeout(pending.timer);
    pending.reject(error);
    connection.pending.delete(id);
  }
  try { if (connection.response) connection.response.destroy(); } catch (_) {}
  try { if (connection.request) connection.request.destroy(); } catch (_) {}
}

function dispatchLegacySseEvent(connection, block) {
  if (legacySseConnections.get(String(connection.upstream.id)) !== connection ||
      !isUpstreamSessionEpochCurrent(connection.upstream.id, connection.sessionEpoch)) return;

  let eventName = 'message';
  const dataLines = [];
  for (const line of String(block || '').split(/\r\n|\r|\n/)) {
    if (!line || line.startsWith(':')) continue;
    if (line.startsWith('event:')) eventName = line.slice(6).trim();
    else if (line.startsWith('data:')) dataLines.push(line.slice(5).replace(/^\s/, ''));
  }
  const data = dataLines.join('\n');
  if (!data) return;

  if (eventName === 'endpoint') {
    try {
      const messageEndpoint = new URL(data.trim(), connection.upstream.url);
      const upstreamOrigin = new URL(connection.upstream.url);
      if (messageEndpoint.origin !== upstreamOrigin.origin || messageEndpoint.username || messageEndpoint.password) {
        closeLegacySseConnection(connection.upstream.id, 'Legacy SSE message endpoint must use the same origin as the configured MCP URL.', connection);
        return;
      }
      connection.messageEndpoint = messageEndpoint.toString();
      if (!connection.ready) {
        connection.ready = true;
        clearTimeout(connection.readyTimer);
        connection.resolveReady(connection);
      }
    } catch (err) {
      closeLegacySseConnection(connection.upstream.id, 'Invalid legacy SSE message endpoint: ' + err.message, connection);
    }
    return;
  }

  let message;
  try { message = JSON.parse(data); } catch (_) { return; }
  if (!message || message.id === undefined || message.id === null) return;

  const key = String(message.id);
  const pending = connection.pending.get(key);
  if (!pending) return;
  clearTimeout(pending.timer);
  connection.pending.delete(key);
  if (message && typeof message === 'object') message.id = pending.originalId;
  pending.resolve({
    ok: !message.error,
    status: 200,
    data: message,
    error: message.error ? (message.error.message || JSON.stringify(message.error)) : null
  });
}

function openLegacySseConnection(
  upstream,
  timeoutMs = 12000,
  expectedSessionEpoch = getUpstreamSessionEpoch(upstream.id)
) {
  const key = String(upstream.id);
  if (!isUpstreamSessionEpochCurrent(key, expectedSessionEpoch))
    return Promise.reject(new Error('MCP session recovery superseded the legacy SSE connection request.'));
  const existing = legacySseConnections.get(key);
  if (existing && !existing.closed && existing.sessionEpoch === expectedSessionEpoch)
    return existing.readyPromise;
  if (existing && !existing.closed)
    closeLegacySseConnection(key, 'Legacy SSE session epoch changed.', existing);

  let targetUrl;
  try { targetUrl = new URL(upstream.url); }
  catch (err) { return Promise.reject(err); }

  let transport;
  try { transport = getHttpTransport(targetUrl); }
  catch (err) { return Promise.reject(err); }

  const connection = {
    upstream,
    sessionEpoch: expectedSessionEpoch,
    request: null,
    response: null,
    messageEndpoint: null,
    pending: new Map(),
    buffer: '',
    ready: false,
    closed: false,
    readyPromise: null,
    resolveReady: null,
    rejectReady: null,
    readyTimer: null
  };

  connection.readyPromise = new Promise((resolve, reject) => {
    connection.resolveReady = resolve;
    connection.rejectReady = reject;
  });
  legacySseConnections.set(key, connection);

  connection.readyTimer = setTimeout(() => {
    closeLegacySseConnection(upstream.id, 'Timed out waiting for legacy SSE endpoint event.', connection);
  }, timeoutMs);

  const headers = createUpstreamHeaders(upstream, {
    'Accept': 'text/event-stream',
    'Cache-Control': 'no-cache'
  });

  const request = transport.get({
    hostname: targetUrl.hostname,
    port: targetUrl.port || (targetUrl.protocol === 'https:' ? 443 : 80),
    path: targetUrl.pathname + targetUrl.search,
    headers
  }, (response) => {
    connection.response = response;
    if (legacySseConnections.get(key) !== connection ||
        !isUpstreamSessionEpochCurrent(key, connection.sessionEpoch)) {
      response.resume();
      closeLegacySseConnection(key, 'Legacy SSE connection was superseded.', connection);
      return;
    }
    const status = response.statusCode || 0;
    if (status < 200 || status >= 300) {
      closeLegacySseConnection(upstream.id, 'Legacy SSE GET returned HTTP ' + status + '.', connection);
      response.resume();
      return;
    }
    if (!String(response.headers['content-type'] || '').toLowerCase().includes('text/event-stream')) {
      closeLegacySseConnection(upstream.id, 'Legacy SSE GET did not return text/event-stream.', connection);
      response.resume();
      return;
    }

    response.setEncoding('utf8');
    response.on('data', (chunk) => {
      connection.buffer += chunk;
      let separator;
      while ((separator = /(?:\r\n|\r|\n)(?:\r\n|\r|\n)/.exec(connection.buffer)) !== null) {
        const block = connection.buffer.slice(0, separator.index);
        connection.buffer = connection.buffer.slice(separator.index + separator[0].length);
        dispatchLegacySseEvent(connection, block);
        if (connection.closed) return;
      }
    });
    response.on('end', () => closeLegacySseConnection(upstream.id, 'Legacy SSE event stream ended.', connection));
    response.on('aborted', () => closeLegacySseConnection(upstream.id, 'Legacy SSE event stream was aborted.', connection));
    response.on('error', (err) => closeLegacySseConnection(upstream.id, err.message, connection));
  });

  connection.request = request;
  trackUpstreamRequest(upstream.id, request);
  request.on('error', (err) => closeLegacySseConnection(upstream.id, err.message, connection));
  return connection.readyPromise;
}

function sendLegacySseRpc(
  upstream,
  rpcPayload,
  timeoutMs = 12000,
  expectedSessionEpoch = getUpstreamSessionEpoch(upstream.id)
) {
  return openLegacySseConnection(upstream, timeoutMs, expectedSessionEpoch).then((connection) => {
    if (!isUpstreamSessionEpochCurrent(upstream.id, expectedSessionEpoch) ||
        legacySseConnections.get(String(upstream.id)) !== connection)
      return { ok: false, status: 0, error: 'MCP legacy SSE response belongs to an obsolete session.' };

    const targetUrl = new URL(connection.messageEndpoint);
    const transport = getHttpTransport(targetUrl);
    const originalId = rpcPayload.id;
    const hasId = originalId !== undefined && originalId !== null;
    const wirePayload = hasId
      ? { ...rpcPayload, id: ++nextLegacySseRpcId }
      : { ...rpcPayload };
    const wireId = wirePayload.id;
    const bodyBuf = Buffer.from(JSON.stringify(wirePayload), 'utf8');
    const headers = createUpstreamHeaders(upstream, {
      'Content-Type': 'application/json',
      'Accept': 'application/json',
      'Content-Length': bodyBuf.length
    });
    let pendingPromise = null;

    if (hasId) {
      pendingPromise = new Promise((resolve, reject) => {
        const key = String(wireId);
        const timer = setTimeout(() => {
          connection.pending.delete(key);
          reject(new Error('Timed out waiting for legacy SSE response to JSON-RPC id ' + key + '.'));
        }, timeoutMs);
        connection.pending.set(key, { resolve, reject, timer, originalId });
      });
      pendingPromise.catch(() => {});
    }

    const postPromise = new Promise((resolve) => {
      const request = transport.request({
        hostname: targetUrl.hostname,
        port: targetUrl.port || (targetUrl.protocol === 'https:' ? 443 : 80),
        path: targetUrl.pathname + targetUrl.search,
        method: 'POST',
        headers,
        timeout: timeoutMs
      }, (response) => {
        let responseBody = '';
        response.setEncoding('utf8');
        response.on('data', chunk => { responseBody += chunk; });
        response.on('end', () => {
          const status = response.statusCode || 0;
          if (status < 200 || status >= 300) {
            resolve({ ok: false, status, error: responseBody || ('Legacy SSE POST returned HTTP ' + status + '.') });
            return;
          }
          const parsed = parseSseOrJson(responseBody);
          resolve({ ok: true, status, data: parsed });
        });
      });
      trackUpstreamRequest(upstream.id, request);
      request.setTimeout(timeoutMs, () => request.destroy(new Error('Legacy SSE POST timed out.')));
      request.on('error', err => resolve({ ok: false, status: 0, error: err.message }));
      request.end(bodyBuf);
    });

    return postPromise.then((postResult) => {
      if (!isUpstreamSessionEpochCurrent(upstream.id, expectedSessionEpoch) ||
          legacySseConnections.get(String(upstream.id)) !== connection)
        return { ok: false, status: 0, error: 'MCP legacy SSE response belongs to an obsolete session.' };
      if (!postResult.ok) {
        if (hasId) {
          const pending = connection.pending.get(String(wireId));
          if (pending) {
            clearTimeout(pending.timer);
            connection.pending.delete(String(wireId));
            pending.resolve({ ok: false, status: postResult.status || 0, error: postResult.error || 'Legacy SSE POST failed.' });
          }
        }
        return postResult;
      }

      if (!hasId) return { ok: true, status: postResult.status || 202, data: postResult.data || null };
      if (postResult.data && String(postResult.data.id) === String(wireId)) {
        const pending = connection.pending.get(String(wireId));
        if (pending) {
          clearTimeout(pending.timer);
          connection.pending.delete(String(wireId));
          if (postResult.data && typeof postResult.data === 'object') postResult.data.id = originalId;
          pending.resolve({ ok: !postResult.data.error, status: postResult.status || 200, data: postResult.data });
        }
      }
      return pendingPromise;
    }).catch((error) => ({ ok: false, error: error.message }));
  }).catch((error) => ({ ok: false, error: error.message }));
}

function tryGetModernSupportedVersions(result) {
  if (!result || typeof result !== 'object' || !Array.isArray(result.supportedVersions))
    return [];
  return result.supportedVersions.filter(value => typeof value === 'string');
}

function buildMcpParameterHeaders(headerBindings, argumentsValue) {
  const headers = {};
  if (!Array.isArray(headerBindings) || !argumentsValue ||
      typeof argumentsValue !== 'object' || Array.isArray(argumentsValue)) return headers;

  for (const binding of headerBindings) {
    let value = argumentsValue;
    for (const part of binding.path || []) {
      if (!value || typeof value !== 'object' || Array.isArray(value) ||
          !Object.prototype.hasOwnProperty.call(value, part)) {
        value = undefined;
        break;
      }
      value = value[part];
    }
    if (value === undefined || value === null) continue;
    if (typeof value !== 'string' && typeof value !== 'boolean' && !Number.isSafeInteger(value)) continue;
    headers['Mcp-Param-' + binding.name] = encodeMcpHeaderValue(String(value));
  }
  return headers;
}

function validateToolHeaderBindings(inputSchema) {
  const bindings = [];
  const names = new Set();
  let invalid = false;

  function containsAnnotationOutsideProperties(value) {
    if (!value || typeof value !== 'object') return false;
    if (Object.prototype.hasOwnProperty.call(value, 'x-mcp-header')) return true;
    if (Array.isArray(value)) return value.some(containsAnnotationOutsideProperties);
    return Object.values(value).some(containsAnnotationOutsideProperties);
  }

  function visit(node, path) {
    if (!node || typeof node !== 'object' || Array.isArray(node)) return;
    if (Object.prototype.hasOwnProperty.call(node, 'x-mcp-header')) {
      const name = node['x-mcp-header'];
      const primitive = ['string', 'integer', 'boolean'].includes(node.type);
      if (typeof name !== 'string' || !/^[!#$%&'*+.^_\x60|~0-9A-Za-z-]+$/.test(name) ||
          !primitive || path.length === 0 || names.has(name.toLowerCase())) {
        invalid = true;
      } else {
        names.add(name.toLowerCase());
        bindings.push({ name, path });
      }
    }

    const properties = node.properties;
    if (properties && typeof properties === 'object' && !Array.isArray(properties)) {
      for (const [key, child] of Object.entries(properties))
        visit(child, path.concat(key));
    }

    for (const [key, child] of Object.entries(node)) {
      if (key !== 'properties' && key !== 'x-mcp-header' && containsAnnotationOutsideProperties(child))
        invalid = true;
    }
  }

  visit(inputSchema, []);
  return { valid: !invalid, bindings };
}

function sendUpstreamRpc(upstream, rpcPayload, timeoutMs = 12000, extraHeaders = null) {
  const key = String(upstream.id);
  const expectedSessionEpoch = getUpstreamSessionEpoch(key);
  const mode = upstreamTransportModes.get(key) || 'auto';
  if (mode === 'legacy-sse')
    return sendLegacySseRpc(upstream, rpcPayload, timeoutMs, expectedSessionEpoch);
  if (mode === 'streamable-http-modern')
    return sendStreamableHttpRpc(upstream, rpcPayload, timeoutMs, true, extraHeaders);
  if (mode === 'streamable-http')
    return sendStreamableHttpRpc(upstream, rpcPayload, timeoutMs, false, null, PROTOCOL_VERSION);

  return sendStreamableHttpRpc(upstream, rpcPayload, timeoutMs).then((result) => {
    if (!isUpstreamSessionEpochCurrent(key, expectedSessionEpoch))
      return { ok: false, status: 0, error: 'MCP session recovery superseded this response.' };
    if (result.ok) {
      if (rpcPayload.method === 'initialize')
        upstreamTransportModes.set(key, 'streamable-http');
      return result;
    }

    const canFallbackToSse = mode === 'auto' &&
      rpcPayload.method === 'initialize' &&
      [400, 404, 405, 406, 415, 501].includes(Number(result.status));
    if (!canFallbackToSse) return result;

    const legacyPayload = {
      ...rpcPayload,
      params: {
        ...(rpcPayload.params || {}),
        protocolVersion: '2024-11-05'
      }
    };

    return sendLegacySseRpc(upstream, legacyPayload, timeoutMs, expectedSessionEpoch).then((sseResult) => {
      if (!isUpstreamSessionEpochCurrent(key, expectedSessionEpoch))
        return { ok: false, status: 0, error: 'MCP session recovery superseded this response.' };
      if (sseResult.ok) {
        upstreamTransportModes.set(key, 'legacy-sse');
        return sseResult;
      }
      return {
        ...sseResult,
        error: 'Streamable HTTP: ' + (result.error || ('HTTP ' + result.status)) +
          '; legacy SSE: ' + (sseResult.error || 'connection failed')
      };
    });
  });
}

function formatUpstreamRpcFailure(stage, response, upstream) {
  const status = Number(response && response.status) || 0;
  const rpcError = response && response.data && response.data.error;
  let detail = '';

  if (rpcError && typeof rpcError === 'object') {
    const code = rpcError.code === undefined ? '' : ' JSON-RPC ' + rpcError.code;
    detail = code + (rpcError.message ? ': ' + String(rpcError.message) : '');
  } else if (response && response.error) {
    detail = String(response.error);
  } else if (response && response.data && response.data.result) {
    detail = 'The response did not contain the expected MCP capability.';
  } else {
    detail = 'No usable JSON-RPC response was received.';
  }

  detail = detail.replace(/Bearer\s+[^\s,;]+/gi, 'Bearer [REDACTED]')
    .replace(/github_pat_[A-Za-z0-9_]+/g, '[REDACTED]')
    .replace(/\bgh[pousr]_[A-Za-z0-9_]+\b/g, '[REDACTED]')
    .replace(/([?&](?:access_token|token|api[_-]?key|secret|authorization|password)=)[^&\s]+/gi, '$1[REDACTED]');

  const headers = upstream && upstream.headers && typeof upstream.headers === 'object'
    ? upstream.headers
    : {};
  for (const [name, value] of Object.entries(headers)) {
    if (!/authorization|token|secret|api[-_]?key|credential/i.test(name)) continue;
    const secret = String(value || '');
    if (secret) detail = detail.split(secret).join('[REDACTED]');
  }

  if (detail.length > 320) detail = detail.slice(0, 317) + '...';
  const proxyNote = proxyConfigurationError
    ? ' Node proxy setup error: ' + proxyConfigurationError
    : ' Node proxy environment: ' + (proxyEnvironmentConfigured ? 'configured.' : 'not detected.');
  return stage + ': ' + (status ? 'HTTP ' + status : 'transport failure') + ' — ' + detail + proxyNote;
}

function logUpstreamFailure(stage, upstream, response, detailOverride = null) {
  const rawErr = String((response && response.error) || detailOverride || '');
  if (/ECONNREFUSED|ECONNRESET|ETIMEDOUT|Upstream request timed out|Upstream socket stalled|socket hang up/i.test(rawErr)) return;
  const rawId = String(upstream && upstream.id ? upstream.id : 'unknown');
  const safeId = rawId.replace(/[^A-Za-z0-9_.:-]/g, '_')
    .replace(/github_pat_[A-Za-z0-9_]+/gi, '[REDACTED]')
    .replace(/\bgh[pousr]_[A-Za-z0-9_]+\b/gi, '[REDACTED]')
    .slice(0, 80);
  const detail = String(detailOverride || formatUpstreamRpcFailure(stage, response, upstream))
    .replace(/[\r\n\t]+/g, ' ')
    .slice(0, 520);
  if (lastUpstreamFailureByServer.get(rawId) === detail) return;
  lastUpstreamFailureByServer.set(rawId, detail);
  process.stderr.write('[MCPBRIDGE-UPSTREAM-FAILURE] serverId=' + safeId + ' ' + detail + '\n');
}

function clearUpstreamFailure(upstreamId) {
  lastUpstreamFailureByServer.delete(String(upstreamId));
}

function invalidateUpstreamSession(
  upstream,
  reason = 'Upstream MCP session became unusable.',
  expectedSessionEpoch = null
) {
  const key = String(upstream && upstream.id ? upstream.id : '');
  if (!key) return false;
  if (expectedSessionEpoch !== null &&
      !isUpstreamSessionEpochCurrent(key, expectedSessionEpoch)) return false;

  const sessionId = upstreamSessions.get(key);
  const mode = upstreamTransportModes.get(key);
  upstreamSessionEpochs.set(key, getUpstreamSessionEpoch(key) + 1);

  upstreamInitialized.delete(key);
  upstreamSessions.delete(key);
  upstreamTransportModes.delete(key);

  if (legacySseConnections.has(key))
    closeLegacySseConnection(key, reason);

  if (sessionId && mode === 'streamable-http' && upstream.transport !== 'stdio')
    void deleteStreamableHttpSession(upstream, sessionId);
  return true;
}

async function ensureUpstreamInitialized(upstream, isCurrent = () => true) {
  const key = String(upstream.id);
  if (!isCurrent()) return { ok: false, error: 'MCP verification was superseded by a configuration change.' };

  if (upstreamInitialized.has(key)) return { ok: true };

  const expectedSessionEpoch = getUpstreamSessionEpoch(key);
  const existing = upstreamInitializationPromises.get(key);
  if (existing && existing.sessionEpoch === expectedSessionEpoch)
    return existing.promise;

  const initialization = (async () => {
    const initReq = {
      jsonrpc: '2.0',
      id: 1,
      method: 'initialize',
      params: {
        protocolVersion: PROTOCOL_VERSION,
        capabilities: {},
        clientInfo: { name: 'MCP Bridge Router', version: '0.9.1' }
      }
    };
    const initTimeoutMs = upstream.scope === 'local' ? 3500 : 20000;
    const res = await sendUpstreamRpc(upstream, initReq, initTimeoutMs);
    if (!isCurrent()) return { ok: false, error: 'MCP verification was superseded by a configuration change.' };
    if (!isUpstreamSessionEpochCurrent(key, expectedSessionEpoch))
      return { ok: false, error: 'MCP initialization was superseded by a session recovery.' };
    if (!res.ok || !res.data || res.data.error || !res.data.result) {
      return {
        ok: false,
        status: Number(res.status) || 0,
        error: 'Upstream MCP initialize failed. ' +
          formatUpstreamRpcFailure('initialize (MCP ' + PROTOCOL_VERSION + ')', res, upstream)
      };
    }

    try {
      await sendUpstreamRpc(
        upstream,
        { jsonrpc: '2.0', method: 'notifications/initialized', params: {} },
        5000
      );
    } catch (_) {}
    if (!isCurrent()) return { ok: false, error: 'MCP verification was superseded by a configuration change.' };
    if (!isUpstreamSessionEpochCurrent(key, expectedSessionEpoch))
      return { ok: false, error: 'MCP initialization was superseded by a session recovery.' };
    upstreamInitialized.add(key);
    return { ok: true };
  })();

  const initializationEntry = {
    sessionEpoch: expectedSessionEpoch,
    promise: initialization
  };
  upstreamInitializationPromises.set(key, initializationEntry);
  try {
    return await initialization;
  } finally {
    if (upstreamInitializationPromises.get(key) === initializationEntry)
      upstreamInitializationPromises.delete(key);
  }
}

async function callUpstreamWithAutoInit(
  upstream,
  rpcPayload,
  timeoutMs = 15000,
  extraHeaders = null,
  isCurrent = () => true
) {
  if (!isCurrent()) return { ok: false, status: 0, error: 'MCP request was superseded by a configuration change.' };
  if (!upstreamInitialized.has(String(upstream.id))) {
    const initialized = await ensureUpstreamInitialized(upstream, isCurrent);
    if (!initialized.ok) return { ok: false, status: 0, error: initialized.error };
  }

  if (!isCurrent()) return { ok: false, status: 0, error: 'MCP request was superseded by a configuration change.' };
  let requestSessionEpoch = getUpstreamSessionEpoch(upstream.id);
  let res = await sendUpstreamRpc(upstream, rpcPayload, timeoutMs, extraHeaders);
  if (!isCurrent()) return { ok: false, status: 0, error: 'MCP request was superseded by a configuration change.' };
  if (!isUpstreamSessionEpochCurrent(upstream.id, requestSessionEpoch))
    return { ok: false, status: 0, error: 'MCP request was superseded by session recovery.' };



  const isToolCallTimeout = rpcPayload && rpcPayload.method === 'tools/call' &&
    !res.ok && /stalled|timed out/i.test(String(res.error || ''));

  if ((!res.ok || (res.data && res.data.error)) && !isToolCallTimeout) {
    invalidateUpstreamSession(
      upstream,
      'Upstream session expired or timed out; re-initializing session.',
      requestSessionEpoch
    );
    const retryStarted = Date.now();
    const initialized = await ensureUpstreamInitialized(upstream, isCurrent);
    if (!initialized.ok) return { ok: false, status: Number(res && res.status) || 0, error: res.error || initialized.error };
    if (!isCurrent()) return { ok: false, status: 0, error: 'MCP request was superseded by a configuration change.' };
    requestSessionEpoch = getUpstreamSessionEpoch(upstream.id);
    res = await sendUpstreamRpc(upstream, rpcPayload, timeoutMs, extraHeaders);
    if (res && typeof res === 'object') {
      res.retriedLatencyMs = Date.now() - retryStarted;
    }
    if (!isUpstreamSessionEpochCurrent(upstream.id, requestSessionEpoch))
      return { ok: false, status: 0, error: 'MCP retry was superseded by session recovery.' };
  }

  if (!res.ok || (res.data && res.data.error)) {
    invalidateUpstreamSession(
      upstream,
      'Upstream transport became unavailable.',
      requestSessionEpoch
    );
  }

  return res;
}

function deleteStreamableHttpSession(upstream, sessionId) {
  return new Promise(resolve => {
    let targetUrl;
    try { targetUrl = new URL(upstream.url); }
    catch (_) { resolve(); return; }

    let transport;
    try { transport = getHttpTransport(targetUrl); }
    catch (_) { resolve(); return; }

    const headers = createUpstreamHeaders(upstream, {
      'MCP-Protocol-Version': PROTOCOL_VERSION,
      'Mcp-Session-Id': String(sessionId)
    });
    const request = transport.request({
      hostname: targetUrl.hostname,
      port: targetUrl.port || (targetUrl.protocol === 'https:' ? 443 : 80),
      path: targetUrl.pathname + targetUrl.search,
      method: 'DELETE',
      headers,
      timeout: 2000
    }, response => {
      response.resume();
      response.on('end', resolve);
      response.on('error', resolve);
    });
    trackUpstreamRequest(upstream.id, request);
    request.setTimeout(2000, () => request.destroy());
    request.on('error', resolve);
    request.end();
  });
}

function rebuildToolRouteMap() {
  toolRouteMap.clear();
  if (!pollManager) return;
  const catalog = stateCache.buildCatalog();
  for (const tool of catalog.tools) {
    const source = tool && tool._meta && tool._meta['com.mcpbridge/source'];
    if (!source || !source.serverId || !source.originalName) continue;
    const worker = pollManager.getWorker(source.serverId);
    if (!worker) continue;
    const headerValidation = (upstreamTransportModes.get(String(source.serverId)) === 'streamable-http-modern')
      ? validateToolHeaderBindings(tool.inputSchema || {})
      : { valid: true, bindings: [] };
    if (!headerValidation.valid) continue;
    toolRouteMap.set(String(tool.name), {
      worker,
      upstream: worker.config,
      originalName: String(source.originalName),
      headerBindings: headerValidation.bindings
    });
  }
}

function handleAggregatedToolsList(rpcId) {
  rebuildToolRouteMap();
  const catalog = stateCache.buildCatalog();
  return {
    jsonrpc: '2.0',
    id: rpcId,
    result: {
      tools: catalog.tools,
      _meta: {
        'com.mcpbridge/catalog': {
          catalog_revision: catalog.catalog_revision,
          cache_revision: catalog.cache_revision,
          servers: catalog.servers
        }
      }
    }
  };
}

async function verifyUpstreamForWorker(upstream) {
  const requestEpoch = getUpstreamRequestEpoch(upstream.id);
  const isCurrent = () => isUpstreamRequestEpochCurrent(upstream.id, requestEpoch);

  if (upstream.pending === true) {
    return {
      pending: true,
      message: 'Waiting for the local MCP process dependency to become available.'
    };
  }

  const cachedStdioTools = (upstream.transport === 'stdio')
    ? stdioKnownToolsByServer.get(String(upstream.id))
    : null;
  const hasCachedStdioTools = Array.isArray(cachedStdioTools) && cachedStdioTools.length > 0;

  if (!hasCachedStdioTools) {
    const initialized = await ensureUpstreamInitialized(upstream, isCurrent);
    if (!initialized.ok) {
      const failure = initialized.error || 'MCP initialize failed.';
      if (/superseded by a configuration change/i.test(failure)) throw new Error(failure);
      logUpstreamFailure('initialize', upstream, {
        ok: false,
        status: Number(initialized.status) || 0,
        error: failure
      }, failure);
      throw new Error(failure);
    }
  }

  const tools = [];
  const seenCursors = new Set();
  let cursor = null;
  let serverInfo = null;
  let pageCount = 0;
  const pollTimeoutMs = upstream.transport === 'stdio' ? 30000 : (upstream.scope === 'local' ? 3500 : 30000);
  let measuredRpcMs = 0;

  if (Array.isArray(cachedStdioTools) && cachedStdioTools.length > 0) {
    tools.push(...cachedStdioTools);
  } else {
    do {
      if (!isCurrent()) throw new Error('MCP verification was superseded by a configuration change.');
      if (++pageCount > 100)
        throw new Error('MCP tools/list exceeded the 100-page safety limit.');

      const params = cursor ? { cursor } : {};
      const callStarted = Date.now();
      const response = await callUpstreamWithAutoInit(
        upstream,
        { jsonrpc: '2.0', id: ++nextUpstreamRpcId, method: 'tools/list', params },
        pollTimeoutMs,
        null,
        isCurrent
      );
      measuredRpcMs += (Date.now() - callStarted);
      if (!isCurrent()) throw new Error('MCP verification was superseded by a configuration change.');
      const result = response && response.data && response.data.result;
      if (!response.ok || !result || !Array.isArray(result.tools)) {
        const errText = formatUpstreamRpcFailure('tools/list', response, upstream);
        if (/superseded by a configuration change/i.test(errText)) throw new Error(errText);
        logUpstreamFailure('tools/list', upstream, response);
        throw new Error(errText);
      }

      if (response.retriedLatencyMs && Number.isFinite(response.retriedLatencyMs)) {
        measuredRpcMs = response.retriedLatencyMs;
      }

      tools.push(...result.tools);
      serverInfo = result.serverInfo ||
        (result._meta && result._meta['io.modelcontextprotocol/serverInfo']) ||
        serverInfo;

      const nextCursor = typeof result.nextCursor === 'string' && result.nextCursor
        ? result.nextCursor
        : null;
      if (nextCursor && seenCursors.has(nextCursor))
        throw new Error('MCP tools/list returned a repeated pagination cursor.');
      if (nextCursor) seenCursors.add(nextCursor);
      cursor = nextCursor;
    } while (cursor);

    if (upstream.transport === 'stdio' && tools.length > 0) {
      stdioKnownToolsByServer.set(String(upstream.id), tools);
      if (upstream.name) {
        const sanitizedName = String(upstream.name).trim().toLowerCase().replace(/[^a-z0-9_-]/g, '_').replace(/^[_-]+|[_-]+$/g, '');
        if (sanitizedName) stdioKnownToolsByServer.set(sanitizedName, tools);
      }
      saveStdioToolsCache();
    }
  }

  if (upstream.transport === 'stdio' && tools.length > 0) {
    const safeKey = String(upstream.id || '').trim().toLowerCase().replace(/[^a-z0-9_-]/g, '_').replace(/^[_-]+|[_-]+$/g, '');
    const netLogPath = options.config
      ? path.join(path.dirname(options.config), 'CustomGateways', 'net-' + safeKey + '.jsonl')
      : null;

    const readNetEventsSince = (sinceTs) => {
      if (!netLogPath || !fs.existsSync(netLogPath)) return [];
      try {
        return fs.readFileSync(netLogPath, 'utf8')
          .split(/\r?\n/)
          .filter(Boolean)
          .map(line => { try { return JSON.parse(line); } catch (_) { return null; } })
          .filter(item => item && Number(item.ts) >= sinceTs);
      } catch (_) {
        return [];
      }
    };

    const buildDummyValue = (propName, propSchema) => {
      const s = propSchema && typeof propSchema === 'object' ? propSchema : {};
      if (s.default !== undefined) return s.default;
      if (Array.isArray(s.enum) && s.enum.length > 0) return s.enum[0];
      if (s.const !== undefined) return s.const;
      const t = Array.isArray(s.type) ? s.type[0] : s.type;
      if (t === 'integer' || t === 'number') return typeof s.minimum === 'number' ? s.minimum : 1;
      if (t === 'boolean') return false;
      if (t === 'array') return [];
      if (t === 'object') return {};
      if (/url|uri|endpoint|link/i.test(String(propName || ''))) return 'https://example.com';
      return 'test';
    };

    const buildProbeArgs = (tool) => {
      const schema = tool && tool.inputSchema && typeof tool.inputSchema === 'object' ? tool.inputSchema : {};
      const props = schema.properties && typeof schema.properties === 'object' ? schema.properties : {};
      const req = Array.isArray(schema.required) ? schema.required : [];
      const args = {};
      for (const key of req) {
        args[key] = buildDummyValue(key, props[key]);
      }
      return args;
    };

    const candidates = tools.filter(t => {
      if (!t || typeof t.name !== 'string') return false;
      if (t.annotations && (t.annotations.destructiveHint === true || t.annotations.readOnlyHint === false || t.annotations.openWorldHint === true)) return false;
      if (/(?:^browser_|screenshot|image|render|thumbnail|viewport|navigate|click|type|press|fill|drag|hover|evaluate|upload|drop|save|export|import|delete|remove|clear|reset|write|create|update|set|exec|run|eval|kill|stop|close|quit|exit|install|download|jump_to|move_|edit_|fetch|search|calc)/i.test(t.name)) {
        return false;
      }
      const schema = t.inputSchema && typeof t.inputSchema === 'object' ? t.inputSchema : {};
      const propsCount = schema.properties && typeof schema.properties === 'object' ? Object.keys(schema.properties).length : 0;
      const reqCount = Array.isArray(schema.required) ? schema.required.length : 0;
      if (propsCount > 0 || reqCount > 0) return false;
      return /^(?:get_|list_|read_|check_|status|ping|health|info|version)/i.test(t.name);
    });

    candidates.sort((a, b) => {
      const getRank = (tool) => {
        const schema = tool.inputSchema && typeof tool.inputSchema === 'object' ? tool.inputSchema : {};
        const propsCount = schema.properties && typeof schema.properties === 'object' ? Object.keys(schema.properties).length : 0;
        const reqCount = Array.isArray(schema.required) ? schema.required.length : 0;
        const isStatusName = /^(?:status|ping|health|check_|get_.*(?:status|info|summary|version|state|scene))/i.test(tool.name);
        if (propsCount === 0 && isStatusName) return 0;
        if (propsCount === 0 && reqCount === 0) return 1;
        if (reqCount === 0 && isStatusName) return 2;
        if (reqCount === 0) return 3;
        return 4;
      };
      return getRank(a) - getRank(b);
    });

        let serverInfo = null;
    const allNetEvents = readNetEventsSince(0);
    const knownLocalPortEvent = [...allNetEvents].reverse().find(e => e.scope === 'local' && Number(e.port) > 0);
    if (Array.isArray(cachedStdioTools) && cachedStdioTools.length > 0 && knownLocalPortEvent) {
      upstream.scope = 'local';
      const worker = pollManager && pollManager.getWorker(upstream.id);
      if (worker) { worker.config.scope = 'local'; worker.intervalMs = 2500; }
      if (upstream.localTargetOffline === true) {
        const err = new Error('ECONNREFUSED: local target application is offline or has no active window');
        err.discoveredTools = cachedStdioTools;
        err.discoveredServerInfo = null;
        throw err;
      }
      let targetPort = Number(knownLocalPortEvent.port);
      if (knownLocalPortEvent.portFile) {
        try {
          if (!fs.existsSync(knownLocalPortEvent.portFile)) {
            const err = new Error('ECONNREFUSED: DevToolsActivePort file not found');
            err.discoveredTools = cachedStdioTools;
            err.discoveredServerInfo = null;
            throw err;
          }
          const pfLines = fs.readFileSync(knownLocalPortEvent.portFile, 'utf8').split(/\r?\n/);
          const pfPort = parseInt((pfLines[0] || '').trim(), 10);
          if (pfPort > 0 && pfPort <= 65535) targetPort = pfPort;
        } catch (pfErr) {
          if (pfErr.discoveredTools) throw pfErr;
        }
      }
      const tcpStart = Date.now();
      const portOpen = await new Promise((resolve) => {
        const sock = new net.Socket();
        let done = false;
        const finish = (ok) => {
          if (done) return;
          done = true;
          sock.destroy();
          resolve(ok);
        };
        sock.setTimeout(600);
        sock.once('connect', () => finish(true));
        sock.once('timeout', () => finish(false));
        sock.once('error', () => finish(false));
        sock.connect(targetPort, '127.0.0.1');
      });
      measuredRpcMs += (Date.now() - tcpStart);
      if (!portOpen) {
        const err = new Error('ECONNREFUSED: local target 127.0.0.1:' + targetPort + ' refused connection (10061)');
        err.discoveredTools = cachedStdioTools;
        err.discoveredServerInfo = null;
        throw err;
      }
      revalidateStdioServerInBackground(upstream, isCurrent);
      clearUpstreamFailure(upstream.id);
      return {
        tools: cachedStdioTools,
        serverInfo: null,
        measuredResponseTimeMs: measuredRpcMs,
        protocolMode: upstreamTransportModes.get(String(upstream.id)) || 'auto'
      };
    }

    const probeTool = (!hasCachedStdioTools && candidates.length > 0) ? candidates[0] : null;
    if (probeTool) {
      const probeArgs = buildProbeArgs(probeTool);
      const probeStart = Date.now();
      const probeRes = await sendUpstreamRpc(
        upstream,
        {
          jsonrpc: '2.0',
          id: ++nextUpstreamRpcId,
          method: 'tools/call',
          params: { name: probeTool.name, arguments: probeArgs }
        },
        25000
      );
      measuredRpcMs += (Date.now() - probeStart);

      const rpcErr = probeRes && probeRes.data && probeRes.data.error;
      if (!probeRes.ok || (rpcErr && Number(rpcErr.code) === -32603)) {
        throw new Error(formatUpstreamRpcFailure('tools/call ' + probeTool.name, probeRes, upstream));
      }

      const netEvents = readNetEventsSince(probeStart - 100);
      const localEvents = netEvents.filter(e => e.scope === 'local');
      const remoteEvents = netEvents.filter(e => e.scope === 'remote');

      if (localEvents.length > 0) {
        upstream.scope = 'local';
        const worker = pollManager && pollManager.getWorker(upstream.id);
        if (worker) { worker.config.scope = 'local'; worker.intervalMs = 2500; }
        const anyLocalOk = localEvents.some(e => e.ok === true);
        if (!anyLocalOk) {
          const lastFail = localEvents[localEvents.length - 1];
          const err = new Error('ECONNREFUSED: local target ' + lastFail.host + ':' + lastFail.port + ' refused connection (' + lastFail.code + ')'); err.discoveredTools = tools; err.discoveredServerInfo = serverInfo; throw err;
        }
      } else if (remoteEvents.length > 0) {
        upstream.scope = 'remote';
        const worker = pollManager && pollManager.getWorker(upstream.id);
        if (worker) { worker.config.scope = 'remote'; worker.intervalMs = 30000; }
        const anyRemoteOk = remoteEvents.some(e => e.ok === true);
        if (!anyRemoteOk) {
          const lastFail = remoteEvents[remoteEvents.length - 1];
          const err = new Error('ECONNREFUSED: remote target ' + lastFail.host + ':' + lastFail.port + ' unreachable (' + lastFail.code + ')'); err.discoveredTools = tools; err.discoveredServerInfo = serverInfo; throw err;
        }
      } else if (upstream.localTargetOffline === true) {
        const err = new Error('ECONNREFUSED: local target application is offline');
        err.discoveredTools = tools;
        err.discoveredServerInfo = serverInfo;
        throw err;
      }
    }
  }

  if (Array.isArray(cachedStdioTools) && cachedStdioTools.length > 0) {
    revalidateStdioServerInBackground(upstream, isCurrent);
  }
  clearUpstreamFailure(upstream.id);
  return {
    tools,
    serverInfo,
    measuredResponseTimeMs: measuredRpcMs,
    protocolMode: upstreamTransportModes.get(String(upstream.id)) || 'auto'
  };
}

async function invokeWorkerTool(upstream, toolName, args, extraHeaders) {
  const key = String(upstream.id);
  const requestEpoch = getUpstreamRequestEpoch(key);
  const isCurrent = () => isUpstreamRequestEpochCurrent(key, requestEpoch);
  if (!upstreamInitialized.has(key)) {
    const initialized = await ensureUpstreamInitialized(upstream, isCurrent);
    if (!initialized.ok) {
      throw new Error(initialized.error || 'The MCP worker could not establish an active session for this server.');
    }
  }

  const requestSessionEpoch = getUpstreamSessionEpoch(key);
  const response = await callUpstreamWithAutoInit(
    upstream,
    {
      jsonrpc: '2.0',
      id: ++nextUpstreamRpcId,
      method: 'tools/call',
      params: {
        name: toolName,
        arguments: args && typeof args === 'object' && !Array.isArray(args) ? args : {}
      }
    },
    60000,
    extraHeaders
  );

  if (!isUpstreamSessionEpochCurrent(key, requestSessionEpoch))
    throw new Error('The MCP tool call was superseded by session recovery.');

  if (!response.ok || !response.data || response.data.error) {
    if ([0, 400, 401, 404, 410, 502, 503, 504].includes(Number(response.status))) {
      invalidateUpstreamSession(
        upstream,
        'MCP tool call failed and the previous session was invalidated.',
        requestSessionEpoch
      );
    }
    logUpstreamFailure('tools/call ' + toolName, upstream, response);
    throw new Error(formatUpstreamRpcFailure('tools/call ' + toolName, response, upstream));
  }
  clearUpstreamFailure(upstream.id);
  return response.data.result;
}

async function handleToolCall(rpcMsg) {
  const params = rpcMsg.params || {};
  const requestedName = String(params.name || '');
  const route = toolRouteMap.get(requestedName);

  if (!route) {
    return {
      jsonrpc: '2.0',
      id: rpcMsg.id,
      result: {
        isError: true,
        content: [{
          type: 'text',
          text: `Инструмент "${requestedName}" отсутствует в последнем опубликованном каталоге MCP Bridge. Запрос к другим серверам для его поиска не выполнялся.`
        }]
      }
    };
  }

  const parameterHeaders = buildMcpParameterHeaders(route.headerBindings, params.arguments);
  try {
    const result = await route.worker.callTool(route.originalName, params.arguments, parameterHeaders);
    return { jsonrpc: '2.0', id: rpcMsg.id, result };
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    logUpstreamFailure('tools/call ' + requestedName, route.upstream, { ok: false, status: 0, error: detail });
    return {
      jsonrpc: '2.0',
      id: rpcMsg.id,
      result: {
        isError: true,
        content: [{
          type: 'text',
          text: `Ошибка вызова "${requestedName}" на сервере "${route.upstream.name}": ${detail}.`
        }]
      }
    };
  }
}

function readRequestBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let total = 0;
    req.on('data', (chunk) => {
      total += chunk.length;
      if (total > MAX_BODY_BYTES) {
        reject(Object.assign(new Error('Payload Too Large'), { statusCode: 413 }));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

const stateCache = new McpServerStateCache();
let pollManager = null;

function promoteUpstreamConnectionState(previous, current) {
  if (!previous || !previous.id || !current || !current.id) return;
  const oldId = String(previous.id);
  const newId = String(current.id);
  if (oldId === newId) return;

  const promotedRequestEpoch = Math.max(
    getUpstreamRequestEpoch(oldId),
    getUpstreamRequestEpoch(newId)
  ) + 1;
  const promotedSessionEpoch = Math.max(
    getUpstreamSessionEpoch(oldId),
    getUpstreamSessionEpoch(newId)
  ) + 1;

  if (upstreamInitialized.delete(oldId)) upstreamInitialized.add(newId);

  for (const source of [
    upstreamSessions,
    upstreamTransportModes,
    lastUpstreamFailureByServer,
    legacySseConnections,
    stdioKnownToolsByServer
  ]) {
    if (!source.has(oldId)) continue;
    const value = source.get(oldId);
    source.delete(oldId);
    source.set(newId, value);
    if (source === legacySseConnections && value) {
      value.upstream = current;
      value.sessionEpoch = promotedSessionEpoch;
    }
  }
  saveStdioToolsCache();

  const temporaryRequests = upstreamRequests.get(oldId);
  if (temporaryRequests) {
    upstreamRequests.delete(oldId);
    const permanentRequests = upstreamRequests.get(newId) || new Set();
    for (const request of temporaryRequests) {
      request.__mcpBridgeServerId = newId;
      permanentRequests.add(request);
    }
    upstreamRequests.set(newId, permanentRequests);
  }

  upstreamInitializationPromises.delete(oldId);
  upstreamInitializationPromises.delete(newId);

  upstreamRequestEpochs.delete(oldId);
  upstreamRequestEpochs.set(newId, promotedRequestEpoch);
  upstreamSessionEpochs.delete(oldId);
  upstreamSessionEpochs.set(newId, promotedSessionEpoch);
}

function resetUpstreamConnectionState(previous, reason, removeFromToolsCache = false) {
  if (!previous || !previous.id) return;
  const key = String(previous.id);
  const oldSession = upstreamSessions.get(key);
  const oldMode = upstreamTransportModes.get(key);
  abortUpstreamRequests(key, reason || 'Upstream configuration changed.');

  if (oldSession && oldMode === 'streamable-http')
    void deleteStreamableHttpSession(previous, oldSession);

  upstreamInitialized.delete(key);
  upstreamInitializationPromises.delete(key);
  upstreamSessions.delete(key);
  upstreamTransportModes.delete(key);
  clearUpstreamFailure(key);
  stdioLastRevalidatedAt.delete(key);
  // preserve stdioKnownToolsByServer across session resets
  if (removeFromToolsCache) {
    let cacheModified = stdioKnownToolsByServer.delete(key);
    if (previous.name) {
      const sanitizedName = String(previous.name).trim().toLowerCase().replace(/[^a-z0-9_-]/g, '_').replace(/^[_-]+|[_-]+$/g, '');
      const stillUsed = upstreams.some(u => u && String(u.id) !== key && u.name &&
        String(u.name).trim().toLowerCase().replace(/[^a-z0-9_-]/g, '_').replace(/^[_-]+|[_-]+$/g, '') === sanitizedName);
      if (sanitizedName && !stillUsed && stdioKnownToolsByServer.delete(sanitizedName))
        cacheModified = true;
    }
    if (cacheModified) saveStdioToolsCache();
  }
  for (const [name, route] of toolRouteMap) {
    if (route && route.upstream && String(route.upstream.id) === key)
      toolRouteMap.delete(name);
  }
}

pollManager = new McpPollManager({
  cache: stateCache,
  verifyServer: verifyUpstreamForWorker,
  invokeTool: invokeWorkerTool,
  onWorkerConfigurationChanged: change => {
    if (change.promoted) {
      promoteUpstreamConnectionState(change.previous, change.current);
      return;
    }
    resetUpstreamConnectionState(
      change.previous,
      'Upstream configuration changed.',
      Boolean(change.removed)
    );
  }
});

pollManager.on('error', error => {
  process.stderr.write('[MCPBRIDGE-POLL-MANAGER-FAILURE] ' + safeError(error) + '\n');
});

stateCache.on('changed', event => {
  if (event && event.event === 'server.polling') return;
  if (event && event.event === 'verification.completed' &&
      event.catalogChanged !== true && event.protocolChanged !== true) return;
  rebuildToolRouteMap();
});

function sendSseEvent(response, eventName, payload) {
  response.write('event: ' + eventName + '\n');
  response.write('data: ' + JSON.stringify(payload) + '\n\n');
}

async function initializePollManager() {
  if (upstreams.length === 0) return;
  await pollManager.replaceConfiguration(upstreams);
}

void initializePollManager().catch(error => {
  process.stderr.write('[MCPBRIDGE-POLL-MANAGER-FAILURE] startup: ' + safeError(error) + '\n');
});

const server = http.createServer(async (req, res) => {
  try {
    const reqUrl = new URL(req.url || '/', `http://${req.headers.host || '127.0.0.1'}`);

    if (reqUrl.pathname.startsWith('/__manager/') &&
        reqUrl.pathname !== '/__manager/health' &&
        !isManagerRequestAuthorized(req)) {
      res.writeHead(404, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      res.end(JSON.stringify({ error: 'Not found.' }));
      return;
    }

    if (req.method === 'GET' && reqUrl.pathname === '/__manager/health') {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ status: 'ok' }));
      return;
    }

    if (req.method === 'GET' && reqUrl.pathname === '/__manager/state') {
      res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      res.end(JSON.stringify({
        servers: stateCache.getAllServers()
      }));
      return;
    }

    if (req.method === 'GET' && reqUrl.pathname === '/__manager/catalog') {
      res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      res.end(JSON.stringify(stateCache.buildCatalog()));
      return;
    }

    if (req.method === 'GET' && reqUrl.pathname === '/__manager/server') {
      const serverId = String(reqUrl.searchParams.get('id') || '');
      const internal = serverId ? stateCache.getInternalServer(serverId) : null;
      if (!internal || internal.temporary) {
        res.writeHead(404, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
        res.end(JSON.stringify({ error: 'MCP server cache entry was not found.' }));
        return;
      }

      res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      res.end(JSON.stringify({
        state: stateCache.getServer(serverId),
        server_revision: internal.serverRevisionToken + ':' + String(internal.catalogRevision),
        discoveredServerName: internal.serverInfo && internal.serverInfo.name
          ? String(internal.serverInfo.name)
          : null,
        toolNames: internal.tools.map(tool => String(tool.name || '')).filter(Boolean),
        tools: internal.tools
      }));
      return;
    }

    if (req.method === 'POST' && reqUrl.pathname === '/__manager/server/call') {
      const bodyText = await readRequestBody(req);
      const parsed = JSON.parse(bodyText || '{}');
      const serverId = String(parsed.serverId || '');
      const toolName = String(parsed.toolName || '');
      const worker = serverId ? pollManager.getWorker(serverId) : null;
      const cached = serverId ? stateCache.getInternalServer(serverId) : null;

      if (!serverId || !toolName || !worker || !cached || !cached.enabled || cached.temporary) {
        res.writeHead(404, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
        res.end(JSON.stringify({ code: 'TOOL_NOT_AVAILABLE', error: 'The selected MCP server worker or cached tool was not found.' }));
        return;
      }

      const currentServerRevision = cached.serverRevisionToken + ':' + String(cached.catalogRevision);
      if (String(parsed.serverRevision || '') !== currentServerRevision) {
        res.writeHead(409, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
        res.end(JSON.stringify({
          code: 'TOOL_VERSION_CHANGED',
          error: 'The selected server changed after its tool was listed. Refresh all_tools and retry with the current server_revision.',
          server_revision: currentServerRevision
        }));
        return;
      }

      const cachedTool = cached.tools.find(tool => tool && tool.name === toolName);
      if (!cachedTool) {
        res.writeHead(404, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
        res.end(JSON.stringify({ code: 'TOOL_NOT_AVAILABLE', error: 'The selected tool is no longer present in the server cache.' }));
        return;
      }

      const args = parsed.arguments && typeof parsed.arguments === 'object' && !Array.isArray(parsed.arguments)
        ? parsed.arguments
        : {};
      const modern = upstreamTransportModes.get(serverId) === 'streamable-http-modern';
      const headerValidation = modern
        ? validateToolHeaderBindings(cachedTool.inputSchema || {})
        : { valid: true, bindings: [] };
      if (!headerValidation.valid) {
        res.writeHead(409, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
        res.end(JSON.stringify({ code: 'INVALID_CACHED_SCHEMA', error: 'The selected tool has invalid MCP parameter header annotations.' }));
        return;
      }

      try {
        const parameterHeaders = buildMcpParameterHeaders(headerValidation.bindings, args);
        const result = await worker.callTool(toolName, args, parameterHeaders);
        res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
        res.end(JSON.stringify({ result }));
      } catch (error) {
        res.writeHead(502, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
        res.end(JSON.stringify({ code: 'UPSTREAM_UNAVAILABLE', error: safeError(error) }));
      }
      return;
    }

    if (req.method === 'POST' && reqUrl.pathname === '/__manager/verification/promote') {
      const bodyText = await readRequestBody(req);
      const parsed = JSON.parse(bodyText || '{}');
      if (!parsed || typeof parsed.temporaryServerId !== 'string' ||
          !parsed.server || typeof parsed.server !== 'object') {
        res.writeHead(400, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ promoted: false, error: 'Temporary server id and permanent server configuration are required.' }));
        return;
      }

      try {
        const promoted = await pollManager.promoteTemporaryVerification(
          parsed.temporaryServerId,
          parsed.server
        );
        rebuildToolRouteMap();
        res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
        res.end(JSON.stringify(promoted));
      } catch (error) {
        res.writeHead(409, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
        res.end(JSON.stringify({ promoted: false, error: safeError(error) }));
      }
      return;
    }

    if (req.method === 'GET' && reqUrl.pathname === '/__manager/verification/cache') {
      const serverId = String(reqUrl.searchParams.get('id') || '');
      try {
        const cached = pollManager.getTemporaryVerificationCache(serverId);
        res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
        res.end(JSON.stringify(cached));
      } catch (error) {
        res.writeHead(404, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
        res.end(JSON.stringify({ error: safeError(error) }));
      }
      return;
    }

    if (req.method === 'DELETE' && reqUrl.pathname === '/__manager/verification/cache') {
      const serverId = String(reqUrl.searchParams.get('id') || '');
      const removed = await pollManager.removeTemporaryVerification(serverId);
      res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      res.end(JSON.stringify(removed));
      return;
    }

    if (req.method === 'GET' && reqUrl.pathname === '/__manager/events') {
      res.writeHead(200, {
        'Content-Type': 'text/event-stream',
        'Cache-Control': 'no-cache, no-store',
        'Connection': 'keep-alive'
      });
      res.write(': connected\n\n');
      const onChanged = event => {
        if (event && event.temporary === true) return;
        sendSseEvent(res, 'cache.changed', event);
      };
      stateCache.on('changed', onChanged);
      res.on('close', () => stateCache.off('changed', onChanged));
      return;
    }

    if (req.method === 'GET' && reqUrl.pathname === '/__manager/upstreams') {
      const safeUpstreams = upstreams.map((upstream) => {
        const safeHeaders = {};
        const sourceHeaders = upstream.headers && typeof upstream.headers === 'object'
          ? upstream.headers
          : {};
        for (const [name, value] of Object.entries(sourceHeaders)) {
          safeHeaders[name] = /authorization|token|secret|api[-_]?key|credential/i.test(name)
            ? '[REDACTED]'
            : value;
        }
        let safeUrl = String(upstream.url || '');
        try {
          const parsedUrl = new URL(safeUrl);
          for (const key of [...parsedUrl.searchParams.keys()]) {
            if (/access[_-]?token|token|secret|api[_-]?key|authorization|credential|password/i.test(key))
              parsedUrl.searchParams.set(key, '[REDACTED]');
          }
          safeUrl = parsedUrl.toString();
        } catch (_) {}
        return {
          id: upstream.id,
          name: upstream.name,
          url: safeUrl,
          enabled: upstream.enabled !== false,
          pending: upstream.pending === true,
          scope: upstream.scope || 'local',
          transport: upstream.transport || 'http',
          headers: safeHeaders
        };
      });
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ upstreams: safeUpstreams }));
      return;
    }

    if (req.method === 'POST' && reqUrl.pathname === '/__manager/upstreams') {
      const bodyText = await readRequestBody(req);
      const parsed = JSON.parse(bodyText || '{}');
      const proxyChanged = applyDynamicProxyConfig(parsed.proxyEnv || {});
      if (!Array.isArray(parsed.upstreams)) {
        res.writeHead(400, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ updated: false, error: 'upstreams must be an array.' }));
        return;
      }

      const nextUpstreams = parsed.upstreams.filter(u =>
        u && u.id && (u.enabled === false || u.pending === true ||
          (typeof u.url === 'string' && u.url.trim()))
      );
      upstreams = nextUpstreams;
      const update = await pollManager.replaceConfiguration(nextUpstreams);
      if (proxyChanged) {
        await restartRemoteHttpWorkers();
      }
      rebuildToolRouteMap();

      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({
        updated: true,
        count: upstreams.length,
        workers: update.count,
        cacheRevision: String(stateCache.cacheRevision)
      }));
      return;
    }

    if (req.method === 'POST' && reqUrl.pathname === '/__manager/verification/request') {
      const bodyText = await readRequestBody(req);
      const parsed = JSON.parse(bodyText || '{}');
      const serverId = String(parsed.serverId || '');
      if (!serverId || !pollManager.getWorker(serverId)) {
        res.writeHead(404, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ accepted: false, error: 'MCP server worker was not found.' }));
        return;
      }

      const cancellation = new AbortController();
      let completed = false;
      res.on('close', () => {
        if (!completed) cancellation.abort();
      });
      res.writeHead(200, {
        'Content-Type': 'text/event-stream',
        'Cache-Control': 'no-cache, no-store',
        'Connection': 'keep-alive'
      });
      res.write(': verification subscribed to existing worker\n\n');
      try {
        const event = await pollManager.subscribeToVerification(serverId, cancellation.signal);
        if (!res.destroyed) {
          sendSseEvent(res, 'verification.completed', event);
          completed = true;
          res.end();
        }
      } catch (error) {
        if (!res.destroyed && !(error && error.name === 'AbortError')) {
          sendSseEvent(res, 'verification.failed', {
            success: false,
            serverId,
            error: safeError(error)
          });
          completed = true;
          res.end();
        }
      }
      return;
    }

    if (req.method === 'POST' && reqUrl.pathname === '/__manager/verification/subscribe') {
      const bodyText = await readRequestBody(req);
      const parsed = JSON.parse(bodyText || '{}');
      if (!parsed || !parsed.server || typeof parsed.server !== 'object') {
        res.writeHead(400, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ accepted: false, error: 'server configuration is required.' }));
        return;
      }

      res.writeHead(200, {
        'Content-Type': 'text/event-stream',
        'Cache-Control': 'no-cache, no-store',
        'Connection': 'keep-alive'
      });
      res.write(': verification subscribed\n\n');
      const verificationId = String(parsed.server.id || '');
      let completionSent = false;
      res.on('close', () => {
        if (!completionSent && verificationId)
          void pollManager.removeTemporaryVerification(verificationId);
      });
      try {
        const event = await pollManager.createTemporaryVerification(parsed.server);
        if (!res.destroyed) sendSseEvent(res, 'verification.completed', event);
        completionSent = true;
      } catch (error) {
        if (!res.destroyed) {
          sendSseEvent(res, 'verification.failed', {
            success: false,
            serverId: verificationId,
            error: safeError(error)
          });
        }
        completionSent = true;
      }
      if (!res.destroyed) res.end();
      return;
    }

    if (req.method === 'DELETE') {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ ok: true }));
      return;
    }

    if (req.method === 'GET') {
      res.writeHead(200, {
        'Content-Type': 'text/event-stream',
        'Cache-Control': 'no-cache',
        'Connection': 'keep-alive'
      });
      res.write(': connected\n\n');
      return;
    }

    if (req.method !== 'POST') {
      res.writeHead(405, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ error: 'Method Not Allowed' }));
      return;
    }

    const rawBody = await readRequestBody(req);
    let rpcMsg = stripRootsCapability(JSON.parse(rawBody));

    if (!rpcMsg || rpcMsg.id === undefined) {
      res.writeHead(202);
      res.end();
      return;
    }

    let responsePayload;
    if (rpcMsg.method === 'initialize') {
      responsePayload = {
        jsonrpc: '2.0',
        id: rpcMsg.id,
        result: {
          protocolVersion: PROTOCOL_VERSION,
          capabilities: {
            tools: {}
          },
          serverInfo: {
            name: 'MCP Bridge Internal Router',
            version: '0.9.1'
          }
        }
      };
    } else if (rpcMsg.method === 'ping') {
      responsePayload = { jsonrpc: '2.0', id: rpcMsg.id, result: {} };
    } else if (rpcMsg.method === 'tools/list') {
      responsePayload = await handleAggregatedToolsList(rpcMsg.id);
    } else if (rpcMsg.method === 'tools/call') {
      responsePayload = await handleToolCall(rpcMsg);
    } else if (rpcMsg.method === 'resources/list') {
      responsePayload = { jsonrpc: '2.0', id: rpcMsg.id, result: { resources: [] } };
    } else if (rpcMsg.method === 'prompts/list') {
      responsePayload = { jsonrpc: '2.0', id: rpcMsg.id, result: { prompts: [] } };
    } else {
      responsePayload = {
        jsonrpc: '2.0',
        id: rpcMsg.id,
        error: { code: -32601, message: `Method not found: ${rpcMsg.method}` }
      };
    }

    res.writeHead(200, {
      'Content-Type': 'application/json',
      'Mcp-Session-Id': req.headers['mcp-session-id'] || 'mcp-bridge-persistent-session'
    });
    res.end(JSON.stringify(responsePayload));
  } catch (err) {
    const code = err && err.statusCode ? err.statusCode : 500;
    res.writeHead(code, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ error: err ? err.message : 'Internal router error' }));
  }
});

server.listen(options.port, options.listen);

async function shutdownRouter() {
  await pollManager.stop();
  server.close(() => process.exit(0));
}
process.once('SIGTERM', () => { void shutdownRouter(); });
process.once('SIGINT', () => { void shutdownRouter(); });