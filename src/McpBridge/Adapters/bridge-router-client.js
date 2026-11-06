'use strict';

const http = require('node:http');
const { URL } = require('node:url');

const MAX_RESPONSE_BYTES = 64 * 1024 * 1024;

class BridgeRouterClient {
  constructor(endpoint) {
    this.endpoint = new URL(endpoint);
    if (this.endpoint.protocol !== 'http:' ||
        !['127.0.0.1', 'localhost'].includes(this.endpoint.hostname)) {
      throw new Error('The internal router endpoint must be HTTP loopback.');
    }
  }

  async getCachedCatalog() {
    return this.getJson('/__manager/catalog');
  }

  async getServerCache(serverId) {
    const url = new URL('/__manager/server', this.endpoint.origin);
    url.searchParams.set('id', String(serverId));
    return this.getJson(url.pathname + url.search);
  }

  async callServerTool(serverId, toolName, serverRevision, args) {
    return this.postJson('/__manager/server/call', {
      serverId: String(serverId),
      toolName: String(toolName),
      serverRevision: String(serverRevision),
      arguments: args && typeof args === 'object' && !Array.isArray(args) ? args : {}
    }, 60000);
  }

  postJson(path, payload, timeoutMs = 60000) {
    return new Promise((resolve, reject) => {
      const body = Buffer.from(JSON.stringify(payload), 'utf8');
      const request = http.request({
        hostname: this.endpoint.hostname,
        port: this.endpoint.port || 80,
        path,
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'application/json',
          'Content-Length': body.length,
          'X-McpBridge-Manager-Key': process.env.MCPBRIDGE_MANAGER_API_KEY || ''
        },
        timeout: timeoutMs
      }, response => {
        const chunks = [];
        let totalBytes = 0;
        response.on('data', chunk => {
          totalBytes += chunk.length;
          if (totalBytes > MAX_RESPONSE_BYTES) {
            request.destroy(new Error('Internal cache response exceeded 64 MiB.'));
            return;
          }
          chunks.push(chunk);
        });
        response.on('end', () => {
          const text = Buffer.concat(chunks).toString('utf8');
          let parsed;
          try { parsed = text ? JSON.parse(text) : {}; }
          catch (_) { reject(new Error('Internal router returned invalid JSON.')); return; }

          const status = response.statusCode || 0;
          if (status < 200 || status >= 300) {
            const error = new Error(String(parsed.error || ('Internal router HTTP ' + status + '.')));
            error.code = String(parsed.code || 'INTERNAL_ROUTER_ERROR');
            error.details = parsed;
            reject(error);
            return;
          }
          resolve(parsed);
        });
      });

      request.on('timeout', () => request.destroy(new Error('Internal server tool call timed out.')));
      request.on('error', reject);
      request.end(body);
    });
  }

  getJson(path, timeoutMs = 15000) {
    return new Promise((resolve, reject) => {
      const request = http.get({
        hostname: this.endpoint.hostname,
        port: this.endpoint.port || 80,
        path,
        headers: {
          Accept: 'application/json',
          'X-McpBridge-Manager-Key': process.env.MCPBRIDGE_MANAGER_API_KEY || ''
        },
        timeout: timeoutMs
      }, response => {
        const chunks = [];
        let totalBytes = 0;
        response.on('data', chunk => {
          totalBytes += chunk.length;
          if (totalBytes > MAX_RESPONSE_BYTES) {
            request.destroy(new Error('Internal cache response exceeded 64 MiB.'));
            return;
          }
          chunks.push(chunk);
        });
        response.on('end', () => {
          const body = Buffer.concat(chunks).toString('utf8');
          const status = response.statusCode || 0;
          let parsed = null;
          try { parsed = body ? JSON.parse(body) : {}; }
          catch (_) { reject(new Error('Internal router returned invalid cache JSON.')); return; }
          if (status < 200 || status >= 300) {
            reject(new Error(String(parsed.error || ('Internal cache API HTTP ' + status + '.'))));
            return;
          }
          resolve(parsed);
        });
      });

      request.on('timeout', () => request.destroy(new Error('Internal cache request timed out.')));
      request.on('error', reject);
    });
  }

}

module.exports = { BridgeRouterClient };