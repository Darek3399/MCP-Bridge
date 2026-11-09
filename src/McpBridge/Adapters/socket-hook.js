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

if (logFile && !global.__mcpBridgeSocketHooked) {
  global.__mcpBridgeSocketHooked = true;
  const origConnect = net.Socket.prototype.connect;
  net.Socket.prototype.connect = function (...args) {
    try {
      let opts = null;
      if (args[0] && typeof args[0] === 'object' && !Array.isArray(args[0])) {
        opts = args[0];
      } else if (typeof args[0] === 'number') {
        opts = { port: args[0], host: typeof args[1] === 'string' ? args[1] : '127.0.0.1' };
      }
      const port = opts && Number(opts.port);
      if (port > 0 && port !== selfPort && !opts.path) {
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
}