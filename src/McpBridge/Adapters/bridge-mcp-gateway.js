#!/usr/bin/env node
'use strict';
[process.stdin, process.stdout, process.stderr].forEach(s => { if (s && typeof s.on === 'function') s.on('error', () => {}); });

const http = require('node:http');
const crypto = require('node:crypto');
const { URL } = require('node:url');
const { BridgeRouterClient } = require('./bridge-router-client');
const { ToolCatalogManager } = require('./bridge-catalog-manager');
const { BridgeToolDispatcher } = require('./bridge-tool-dispatcher');

const MODERN_PROTOCOL_VERSION = '2026-07-28';
const LEGACY_PROTOCOL_VERSIONS = new Set(['2025-11-25', '2025-06-18', '2025-03-26']);
const SERVER_INFO = { name: 'MCP Bridge Gateway', version: '0.9.1' };
const MAX_BODY_BYTES = 64 * 1024 * 1024;
const SESSION_TTL_MS = 12 * 60 * 60 * 1000;

function parseArgs(argv) {
  const result = {
    listen: '127.0.0.1',
    port: 8004,
    router: 'http://127.0.0.1:8001/mcp'
  };
  for (let i = 2; i < argv.length; i++) {
    const key = argv[i];
    const value = argv[i + 1];
    if (key === '--listen' && value) { result.listen = value; i++; }
    else if (key === '--port' && value) { result.port = Number(value); i++; }
    else if (key === '--router' && value) { result.router = value; i++; }
  }
  return result;
}

function readRequestBody(request) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let total = 0;
    request.on('data', chunk => {
      total += chunk.length;
      if (total > MAX_BODY_BYTES) {
        reject(Object.assign(new Error('Payload Too Large'), { statusCode: 413 }));
        request.destroy();
        return;
      }
      chunks.push(chunk);
    });
    request.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    request.on('error', reject);
  });
}

function sendJson(response, statusCode, payload, headers = {}) {
  const body = JSON.stringify(payload);
  response.writeHead(statusCode, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
    ...headers
  });
  response.end(body);
}

function rpcError(id, code, message, data) {
  return {
    jsonrpc: '2.0',
    id: id === undefined ? null : id,
    error: {
      code,
      message,
      ...(data === undefined ? {} : { data })
    }
  };
}

function logGatewayFailure(method, error) {
  const detail = String(error instanceof Error ? error.message : error)
    .replace(/(Bearer\s+)[^\s,;]+/gi, '$1[REDACTED]')
    .replace(/github_pat_[A-Za-z0-9_]+/gi, '[REDACTED]')
    .replace(/\bgh[pousr]_[A-Za-z0-9_]+\b/gi, '[REDACTED]')
    .replace(/((?:authorization|token|secret|api[_-]?key)\s*[:=]\s*)[^\s,;]+/gi, '$1[REDACTED]')
    .replace(/([?&](?:access_token|token|api[_-]?key|secret|authorization)=)[^&\s]+/gi, '$1[REDACTED]')
    .replace(/[\r\n\t]+/g, ' ')
    .slice(0, 320);
  process.stderr.write('[MCPBRIDGE-GATEWAY-FAILURE] method=' + String(method) + ' ' + detail + '\n');
}

function textResult(value) {
  const serialized = JSON.stringify(value);
  return {
    content: [{ type: 'text', text: serialized }],
    structuredContent: value
  };
}

function getModernProtocolVersion(request, rpcMessage) {
  const headerVersion = String(request.headers['mcp-protocol-version'] || '').trim();
  const params = rpcMessage && rpcMessage.params && typeof rpcMessage.params === 'object'
    ? rpcMessage.params
    : {};
  const requestMeta = params._meta && typeof params._meta === 'object' ? params._meta : {};
  const metaVersion = String(requestMeta['io.modelcontextprotocol/protocolVersion'] || '').trim();
  const clientCapabilities = requestMeta['io.modelcontextprotocol/clientCapabilities'];
  const hasClientCapabilities = clientCapabilities !== null &&
    typeof clientCapabilities === 'object' && !Array.isArray(clientCapabilities);

  if (headerVersion && metaVersion && headerVersion !== metaVersion) {
    return { error: 'MCP-Protocol-Version header does not match request _meta protocolVersion.' };
  }

  return {
    version: metaVersion || headerVersion,
    headerVersion,
    metaVersion,
    requestMeta,
    hasClientCapabilities
  };
}

function withModernResultMetadata(result) {
  if (!result || typeof result !== 'object' || Array.isArray(result)) return result;
  const existingMeta = result._meta && typeof result._meta === 'object' && !Array.isArray(result._meta)
    ? result._meta
    : {};
  return {
    ...result,
    resultType: result.resultType || 'complete',
    _meta: {
      ...existingMeta,
      'io.modelcontextprotocol/serverInfo': SERVER_INFO
    }
  };
}

function sendUnsupportedModernVersion(response, id, requestedVersion) {
  sendJson(response, 200, rpcError(
    id,
    -32022,
    'Unsupported protocol version.',
    {
      supported: [MODERN_PROTOCOL_VERSION],
      requested: requestedVersion || null
    }
  ), { 'MCP-Protocol-Version': MODERN_PROTOCOL_VERSION });
}

const TOOLS = [
  {
    name: 'all_tools',
    title: 'Get the live Bridge tool catalog',
    description:
      'Returns the latest per-server tool catalog snapshot published by the independent MCP Poll Manager. ' +
      'This operation reads the cache only: it does not probe, initialize, or wait for any upstream server. ' +
      'The catalog includes stable tool_id values, complete inputSchema JSON Schemas, source server information, ' +
      'per-server status, staleness and catalog_revision.',
    inputSchema: {
      type: 'object',
      properties: {},
      additionalProperties: false
    }
  },
  {
    name: 'call_tool',
    title: 'Call a tool through MCP Bridge',
    description:
      'Executes one tool using the current cached descriptor and routes directly to that tool’s upstream worker. ' +
      'It does not refresh or probe the global catalog and does not wait for checks of other servers. ' +
      'Arguments are validated against the selected server’s latest cached inputSchema. The Bridge resolves ' +
      'the current server route and protects dispatch against concurrent cache changes internally. ' +
      'Do not automatically repeat operations that may have side effects after an upstream failure.',
    inputSchema: {
      type: 'object',
      properties: {
        tool_id: {
          type: 'string',
          description: 'Exact tool_id returned by all_tools.'
        },
        catalog_revision: {
          type: 'string',
          description: 'Global catalog revision returned by all_tools; informational and not used as a server lock.'
        },
        arguments: {
          type: 'object',
          description: 'Arguments matching the selected tool inputSchema.',
          additionalProperties: true
        }
      },
      required: ['tool_id', 'catalog_revision', 'arguments'],
      additionalProperties: false
    }
  }
];

const options = parseArgs(process.argv);
const routerClient = new BridgeRouterClient(options.router);
const catalogManager = new ToolCatalogManager(routerClient);
const dispatcher = new BridgeToolDispatcher(routerClient, catalogManager);
const sessions = new Map();

function touchSession(sessionId) {
  if (!sessionId) return null;
  const session = sessions.get(sessionId);
  if (!session) return null;
  session.lastActivity = Date.now();
  return session;
}

function pruneSessions() {
  const cutoff = Date.now() - SESSION_TTL_MS;
  for (const [id, session] of sessions) {
    if (session.lastActivity < cutoff) sessions.delete(id);
  }
}

async function handleToolCall(params) {
  const args = params && params.arguments && typeof params.arguments === 'object'
    ? params.arguments
    : {};

  if (params.name === 'all_tools') {
    const snapshot = await catalogManager.getSnapshot();
    return textResult(snapshot);
  }

  if (params.name === 'call_tool') {
    const result = await dispatcher.call({
      toolId: String(args.tool_id || ''),
      catalogRevision: String(args.catalog_revision || ''),
      arguments: args.arguments
    });
    return result;
  }

  return {
    isError: true,
    content: [{ type: 'text', text: 'Unknown Bridge tool: ' + String(params.name || '') }]
  };
}

async function handleRpc(request, response, rpcMessage) {
  const sessionId = String(request.headers['mcp-session-id'] || '');
  const session = sessionId ? touchSession(sessionId) : null;
  const requestVersion = getModernProtocolVersion(request, rpcMessage);

  if (requestVersion.error) {
    sendJson(response, 400, rpcError(rpcMessage.id, -32600, requestVersion.error));
    return;
  }

  const requestedVersion = requestVersion.version;
  const isDiscover = rpcMessage.method === 'server/discover';
  const isModern = isDiscover || requestedVersion === MODERN_PROTOCOL_VERSION;

  if (requestedVersion && requestedVersion !== MODERN_PROTOCOL_VERSION &&
      !LEGACY_PROTOCOL_VERSIONS.has(requestedVersion)) {
    sendUnsupportedModernVersion(response, rpcMessage.id, requestedVersion);
    return;
  }

  if (isModern && requestedVersion !== MODERN_PROTOCOL_VERSION) {
    sendUnsupportedModernVersion(response, rpcMessage.id, requestedVersion);
    return;
  }

  if (isModern) {
    if (!requestVersion.headerVersion || !requestVersion.metaVersion ||
        !requestVersion.hasClientCapabilities) {
      sendJson(response, 400, rpcError(
        rpcMessage.id,
        -32600,
        'Modern requests must include MCP-Protocol-Version and params._meta protocolVersion and clientCapabilities.'
      ));
      return;
    }

    const methodHeader = String(request.headers['mcp-method'] || '').trim();
    if (!methodHeader || methodHeader !== rpcMessage.method) {
      sendJson(response, 400, rpcError(
        rpcMessage.id,
        -32020,
        'Mcp-Method header is missing or does not match the JSON-RPC method.'
      ));
      return;
    }

    const requiresNameHeader = rpcMessage.method === 'tools/call';
    if (requiresNameHeader) {
      const namedMethodValue = String((rpcMessage.params && rpcMessage.params.name) || '');
      const nameHeader = String(request.headers['mcp-name'] || '');
      if (!namedMethodValue || !nameHeader || nameHeader !== namedMethodValue) {
        sendJson(response, 400, rpcError(
          rpcMessage.id,
          -32020,
          'Mcp-Name header is missing or does not match params.name.'
        ));
        return;
      }
    }
  }

  if (!isModern && sessionId && !session && rpcMessage.method !== 'initialize') {
    sendJson(response, 404, { error: 'Unknown or expired MCP session.' });
    return;
  }

  if (!isModern && session && requestedVersion &&
      requestedVersion !== session.protocolVersion) {
    sendJson(response, 400, rpcError(
      rpcMessage.id,
      -32600,
      'MCP-Protocol-Version does not match the negotiated session version.'
    ));
    return;
  }

  if (!isModern && !session && rpcMessage.method !== 'initialize') {
    sendJson(response, 400, rpcError(
      rpcMessage.id,
      -32600,
      'Legacy MCP requests require an initialized session. Modern clients must send per-request _meta.'
    ));
    return;
  }

  if (!isModern && rpcMessage.id === undefined && rpcMessage.method === 'notifications/initialized') {
    response.writeHead(202, { 'Cache-Control': 'no-store' });
    response.end();
    return;
  }

  if (rpcMessage.id === undefined || rpcMessage.id === null) {
    response.writeHead(202, { 'Cache-Control': 'no-store' });
    response.end();
    return;
  }

  let result;
  let responseSessionId = isModern ? null : (sessionId || null);
  let negotiatedLegacyVersion = session ? session.protocolVersion : null;

  switch (rpcMessage.method) {
    case 'server/discover': {
      const metaVersion = requestedVersion;
      if (!metaVersion) {
        sendJson(response, 400, rpcError(
          rpcMessage.id,
          -32602,
          'server/discover requires params._meta.io.modelcontextprotocol/protocolVersion.'
        ));
        return;
      }

      result = {
        resultType: 'complete',
        supportedVersions: [...LEGACY_PROTOCOL_VERSIONS, MODERN_PROTOCOL_VERSION],
        capabilities: { tools: {} },
        _meta: { 'io.modelcontextprotocol/serverInfo': SERVER_INFO },
        instructions:
          'This MCP endpoint intentionally exposes exactly two stable tools: all_tools and call_tool. ' +
          'Use all_tools to obtain the current internal tool catalog, then call_tool to execute a selected tool.',
        ttlMs: 3600000,
        cacheScope: 'private'
      };
      break;
    }

    case 'initialize': {
      const requestedLegacyVersion = String(
        (rpcMessage.params && rpcMessage.params.protocolVersion) || ''
      ).trim();

      if (requestedLegacyVersion === MODERN_PROTOCOL_VERSION) {
        sendJson(response, 400, rpcError(
          rpcMessage.id,
          -32022,
          'This version uses per-request metadata and server/discover, not initialize.',
          { supported: [MODERN_PROTOCOL_VERSION], requested: requestedLegacyVersion }
        ));
        return;
      }

      if (requestVersion.headerVersion && requestedLegacyVersion &&
          requestVersion.headerVersion !== requestedLegacyVersion) {
        sendJson(response, 400, rpcError(
          rpcMessage.id,
          -32602,
          'MCP-Protocol-Version header does not match initialize params.protocolVersion.'
        ));
        return;
      }

      const selectedVersion = requestedLegacyVersion || '2025-06-18';
      if (!LEGACY_PROTOCOL_VERSIONS.has(selectedVersion)) {
        sendJson(response, 400, rpcError(
          rpcMessage.id,
          -32602,
          'Unsupported legacy protocol version.',
          { supported: [...LEGACY_PROTOCOL_VERSIONS], requested: selectedVersion }
        ));
        return;
      }

      negotiatedLegacyVersion = selectedVersion;
      responseSessionId = crypto.randomUUID();
      sessions.set(responseSessionId, {
        createdAt: Date.now(),
        lastActivity: Date.now(),
        protocolVersion: selectedVersion
      });

      result = {
        protocolVersion: selectedVersion,
        capabilities: { tools: {} },
        serverInfo: SERVER_INFO,
        instructions:
          'This MCP endpoint intentionally exposes exactly two stable tools. Use all_tools to discover the current ' +
          'internal catalog, then call_tool to execute a selected tool.'
      };
      break;
    }

    case 'tools/list':
      result = { tools: TOOLS };
      if (isModern) {
        result.ttlMs = 3600000;
        result.cacheScope = 'private';
      }
      break;

    case 'tools/call':
      try {
        result = await handleToolCall(rpcMessage.params || {});
      } catch (error) {
        logGatewayFailure('tools/call', error);
        result = {
          isError: true,
          content: [{
            type: 'text',
            text: 'Bridge tool execution failed: ' +
              (error instanceof Error ? error.message : String(error))
          }]
        };
      }
      break;

    case 'ping':
      if (isModern) {
        sendJson(response, 200, rpcError(rpcMessage.id, -32601, 'Method not found: ping'),
          { 'MCP-Protocol-Version': MODERN_PROTOCOL_VERSION });
        return;
      }
      result = {};
      break;

    default:
      sendJson(response, 200, rpcError(rpcMessage.id, -32601, 'Method not found: ' + rpcMessage.method),
        {
          ...(responseSessionId ? { 'Mcp-Session-Id': responseSessionId } : {}),
          ...(isModern ? { 'MCP-Protocol-Version': MODERN_PROTOCOL_VERSION } : {})
        });
      return;
  }

  if (isModern && rpcMessage.method === 'server/discover') {
    result.ttlMs = 3600000;
    result.cacheScope = 'private';
  }

  const responseResult = isModern ? withModernResultMetadata(result) : result;
  const responseHeaders = isModern
    ? { 'MCP-Protocol-Version': MODERN_PROTOCOL_VERSION }
    : (responseSessionId
      ? { 'Mcp-Session-Id': responseSessionId, 'MCP-Protocol-Version': negotiatedLegacyVersion }
      : {});

  sendJson(response, 200, {
    jsonrpc: '2.0',
    id: rpcMessage.id,
    result: responseResult
  }, responseHeaders);
}

const server = http.createServer(async (request, response) => {
  pruneSessions();
  try {
    const requestUrl = new URL(request.url || '/', 'http://' + (request.headers.host || '127.0.0.1'));
    const pathname = requestUrl.pathname;

    if (request.method === 'GET' && pathname === '/__manager/health') {
      sendJson(response, 200, {
        status: 'ok',
        service: 'MCP Bridge Gateway',
        protocolVersion: MODERN_PROTOCOL_VERSION,
        supportedProtocolVersions: [MODERN_PROTOCOL_VERSION, ...LEGACY_PROTOCOL_VERSIONS],
        externalTools: TOOLS.map(tool => tool.name),
        catalogRevision: String(catalogManager.revision),
        catalogMode: 'poll-manager-cache'
      });
      return;
    }

    if (pathname !== '/mcp') {
      sendJson(response, 404, { error: 'Not Found' });
      return;
    }

    if (request.method === 'GET') {
      response.writeHead(405, {
        'Allow': 'POST, DELETE',
        'Cache-Control': 'no-store'
      });
      response.end();
      return;
    }

    if (request.method === 'DELETE') {
      const sessionId = String(request.headers['mcp-session-id'] || '');
      if (sessionId) sessions.delete(sessionId);
      response.writeHead(200, { 'Cache-Control': 'no-store' });
      response.end();
      return;
    }

    if (request.method !== 'POST') {
      response.writeHead(405, { 'Allow': 'POST, DELETE' });
      response.end();
      return;
    }

    const rawBody = await readRequestBody(request);
    let rpcMessage;
    try { rpcMessage = JSON.parse(rawBody); }
    catch (_) {
      sendJson(response, 400, rpcError(null, -32700, 'Parse error.'));
      return;
    }

    if (!rpcMessage || rpcMessage.jsonrpc !== '2.0' || typeof rpcMessage.method !== 'string') {
      sendJson(response, 400, rpcError(rpcMessage && rpcMessage.id, -32600, 'Invalid JSON-RPC request.'));
      return;
    }

    await handleRpc(request, response, rpcMessage);
  } catch (error) {
    logGatewayFailure('http-request', error);
    const status = error && error.statusCode ? error.statusCode : 500;
    if (!response.headersSent) {
      sendJson(response, status, { error: error instanceof Error ? error.message : String(error) });
    } else {
      response.destroy();
    }
  }
});

server.on('error', error => {
  process.stderr.write('Bridge MCP Gateway server error: ' + error.message + '\n');
});

server.listen(options.port, options.listen);