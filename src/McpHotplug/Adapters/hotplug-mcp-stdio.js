#!/usr/bin/env node
'use strict';

const readline = require('node:readline');
const { HotplugRouterClient } = require('./hotplug-router-client');
const { ToolCatalogManager } = require('./hotplug-catalog-manager');
const { HotplugToolDispatcher } = require('./hotplug-tool-dispatcher');

const SUPPORTED_VERSIONS = new Set(['2025-11-25', '2025-06-18', '2025-03-26', '2024-11-05']);
const DEFAULT_PROTOCOL_VERSION = '2025-06-18';
const SERVER_INFO = { name: 'MCP Hotplug Gateway', version: '0.9.89' };

const TOOLS = [
  {
    name: 'all_tools',
    title: 'Get the live Hotplug tool catalog',
    description:
      'Returns the latest per-server tool catalog snapshot published by the independent MCP Poll Manager. ' +
      'This operation reads the cache only: it does not probe, initialize, or wait for any upstream server. ' +
      'The catalog includes stable tool_id values, complete inputSchema JSON Schemas, source server information, ' +
      'per-server status, staleness, catalog_revision, and compact name-only disabled_tools lists (tools disabled by the user to save tokens; ask the user to enable one if needed).',
    inputSchema: {
      type: 'object',
      properties: {},
      additionalProperties: false
    }
  },
  {
    name: 'call_tool',
    title: 'Call a tool through MCP Hotplug',
    description:
      'Executes one tool using the current cached descriptor and routes directly to that tool’s upstream worker. ' +
      'It does not refresh or probe the global catalog and does not wait for checks of other servers. ' +
      'Arguments are validated against the selected server’s latest cached inputSchema. MCP Hotplug resolves ' +
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

function parseArgs(argv) {
  let router = 'http://127.0.0.1:8001/mcp';
  for (let i = 2; i < argv.length; i++) {
    if (argv[i] === '--router' && argv[i + 1]) {
      router = argv[i + 1];
      i++;
    }
  }
  return { router };
}

const options = parseArgs(process.argv);
const routerClient = new HotplugRouterClient(options.router);
const catalogManager = new ToolCatalogManager(routerClient);
const dispatcher = new HotplugToolDispatcher(routerClient, catalogManager);

function writeMessage(msg) {
  process.stdout.write(JSON.stringify(msg) + '\n');
}

function writeError(id, code, message, data) {
  writeMessage({
    jsonrpc: '2.0',
    id: id === undefined ? null : id,
    error: { code, message, ...(data === undefined ? {} : { data }) }
  });
}

async function handleMessage(msg) {
  if (!msg || msg.jsonrpc !== '2.0' || typeof msg.method !== 'string') {
    if (msg && msg.id !== undefined) writeError(msg.id, -32600, 'Invalid Request');
    return;
  }

  if (msg.id === undefined || msg.id === null) return;

  switch (msg.method) {
    case 'initialize': {
      const requested = String((msg.params && msg.params.protocolVersion) || '').trim();
      const protocolVersion = SUPPORTED_VERSIONS.has(requested) ? requested : DEFAULT_PROTOCOL_VERSION;
      writeMessage({
        jsonrpc: '2.0',
        id: msg.id,
        result: {
          protocolVersion,
          capabilities: { tools: {} },
          serverInfo: SERVER_INFO,
          instructions:
            'This MCP endpoint intentionally exposes exactly two stable tools: all_tools and call_tool. ' +
            'Use all_tools to obtain the current internal tool catalog, then call_tool to execute a selected tool.'
        }
      });
      return;
    }

    case 'ping':
      writeMessage({ jsonrpc: '2.0', id: msg.id, result: {} });
      return;

    case 'tools/list':
      writeMessage({ jsonrpc: '2.0', id: msg.id, result: { tools: TOOLS } });
      return;

    case 'tools/call': {
      const params = msg.params && typeof msg.params === 'object' ? msg.params : {};
      const args = params.arguments && typeof params.arguments === 'object' ? params.arguments : {};
      try {
        let result;
        if (params.name === 'all_tools') {
          const snapshot = await catalogManager.getSnapshot();
          result = {
            content: [{ type: 'text', text: JSON.stringify(snapshot) }],
            structuredContent: snapshot
          };
        } else if (params.name === 'call_tool') {
          result = await dispatcher.call({
            toolId: String(args.tool_id || ''),
            catalogRevision: String(args.catalog_revision || ''),
            arguments: args.arguments
          });
        } else {
          result = {
            isError: true,
            content: [{ type: 'text', text: 'Unknown Hotplug tool: ' + String(params.name || '') }]
          };
        }
        writeMessage({ jsonrpc: '2.0', id: msg.id, result });
      } catch (error) {
        writeMessage({
          jsonrpc: '2.0',
          id: msg.id,
          result: {
            isError: true,
            content: [{
              type: 'text',
              text: 'Hotplug tool execution failed: ' + (error instanceof Error ? error.message : String(error))
            }]
          }
        });
      }
      return;
    }

    default:
      writeError(msg.id, -32601, 'Method not found: ' + msg.method);
  }
}

const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
rl.on('line', line => {
  const trimmed = line.trim();
  if (!trimmed) return;
  let parsed;
  try {
    parsed = JSON.parse(trimmed);
  } catch (_) {
    writeError(null, -32700, 'Parse error');
    return;
  }
  Promise.resolve(handleMessage(parsed)).catch(err => {
    process.stderr.write('[MCPHOTPLUG-STDIO-FAILURE] ' + (err instanceof Error ? err.message : String(err)) + '\n');
  });
});