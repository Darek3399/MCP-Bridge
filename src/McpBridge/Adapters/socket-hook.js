'use strict';
const net = require('node:net');
const fs = require('node:fs');

const logFile = process.env.MCPBRIDGE_NET_LOG;
const selfPort = Number(process.env.MCPBRIDGE_SELF_PORT || 0);

function isLoopback(host) {
  const h = String(host || 'localhost').toLowerCase().trim();
  return h === 'localhost' || h === '127.0.0.1' || h === '::1' || h === '[::1]' || h.startsWith('127.');
}

function writeLog(entry) {
  if (!logFile) return;
  try {
    fs.appendFileSync(logFile, JSON.stringify(entry) + '\n', 'utf8');
  } catch (_) {}
}

function extractConnectOptions(args) {
  const first = Array.isArray(args[0]) ? args[0][0] : args[0];
  const second = Array.isArray(args[0]) ? args[0][1] : args[1];
  if (first && typeof first === 'object' && !Array.isArray(first)) {
    return first;
  }
  if (typeof first === 'number' || (typeof first === 'string' && /^\d+$/.test(first.trim()))) {
    return {
      port: Number(first),
      host: typeof second === 'string' ? second : '127.0.0.1'
    };
  }
  return null;
}

function tryLogDevToolsPortFile(filePath, content, ok) {
  try {
    const p = String(filePath || '');
    if (!/DevToolsActivePort$/i.test(p)) return;
    let port = 9222;
    if (typeof content === 'string' && content.trim()) {
      const firstLine = content.split(/\r?\n/)[0].trim();
      const parsed = parseInt(firstLine, 10);
      if (parsed > 0 && parsed <= 65535) port = parsed;
    }
    writeLog({
      ts: Date.now(),
      host: '127.0.0.1',
      port,
      scope: 'local',
      ok: Boolean(ok),
      code: ok ? 'OK' : 'ENOENT',
      portFile: p
    });
  } catch (_) {}
}

if (logFile && !global.__mcpBridgeSocketHooked) {
  global.__mcpBridgeSocketHooked = true;

  const origConnect = net.Socket.prototype.connect;
  net.Socket.prototype.connect = function (...args) {
    try {
      const opts = extractConnectOptions(args);
      const port = opts && Number(opts.port);
      if (port > 0 && port <= 65535 && port !== selfPort && !opts.path) {
        const host = String(opts.host || opts.hostname || '127.0.0.1');
        const scope = isLoopback(host) ? 'local' : 'remote';
        const onConnect = () => {
          cleanup();
          writeLog({ ts: Date.now(), host, port, scope, ok: true, code: 'OK' });
        };
        const onError = (err) => {
          cleanup();
          const code = (err && (err.code || err.errno || err.message)) ? String(err.code || err.errno || err.message) : 'ERROR';
          writeLog({ ts: Date.now(), host, port, scope, ok: false, code });
        };
        const cleanup = () => {
          this.removeListener('connect', onConnect);
          this.removeListener('error', onError);
        };
        this.once('connect', onConnect);
        this.once('error', onError);
      }
    } catch (_) {}
    return origConnect.apply(this, args);
  };

  if (fs.promises && typeof fs.promises.readFile === 'function') {
    const origAsyncRead = fs.promises.readFile;
    fs.promises.readFile = async function (filePath, ...rest) {
      try {
        const res = await origAsyncRead.call(this, filePath, ...rest);
        tryLogDevToolsPortFile(filePath, typeof res === 'string' ? res : (Buffer.isBuffer(res) ? res.toString('utf8') : ''), true);
        return res;
      } catch (err) {
        tryLogDevToolsPortFile(filePath, '', false);
        throw err;
      }
    };
  }
}