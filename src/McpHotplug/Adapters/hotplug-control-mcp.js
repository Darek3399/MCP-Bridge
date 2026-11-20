'use strict';

const toolPolicy = require('./mcp-tool-policy');

const PROTOCOL_VERSION = '2025-06-18';
const SERVER_INFO = { name: 'Hotplug Control MCP', version: '1.0.0' };

const TOOLS = [
  {
    name: toolPolicy.HOTPLUG_CONTROL_LIST_TOOL,
    description:
      'List MCP servers, exact full category/toolset names, and tool names with enabled/disabled status (name-only, no JSON schemas). ' +
      'Use only when inspecting capabilities before trimming for a task or finding a missing disabled tool; avoid unnecessary repeat calls to preserve per-turn tool-call limits.',
    inputSchema: {
      type: 'object',
      properties: {
        server_id: {
          type: 'string',
          description: 'Optional MCP server ID. Omit to list all servers (or search across all servers when search is set).'
        },
        categories: {
          type: 'array',
          items: { type: 'string' },
          description: 'Optional exact full category/toolset names (requires server_id) to list their internal tool names.'
        },
        status: {
          type: 'string',
          enum: ['all', 'enabled', 'disabled'],
          description: 'Filter by state (default: "all").'
        },
        search: {
          type: 'string',
          description: 'Optional case-insensitive substring filter on exact full category/tool names.'
        }
      },
      additionalProperties: false
    }
  },
  {
    name: toolPolicy.HOTPLUG_CONTROL_MANAGE_TOOL,
    description:
      'Enable/disable MCP servers, exact full categories/toolsets, or specific tools to cut token usage or unlock disabled tools. ' +
      'Batch all changes for a task into ONE call instead of toggling piecemeal across turns (saves tool-call limits). ' +
      'Exact full names required. Cannot disable hotplug_control itself. Changes persist and support Ctrl+Z/Y in UI.',
    inputSchema: {
      type: 'object',
      properties: {
        servers: {
          type: 'object',
          description: 'Optional server-level toggles (cannot disable hotplug_control).',
          properties: {
            enable: { type: 'array', items: { type: 'string' }, description: 'Server IDs to enable.' },
            disable: { type: 'array', items: { type: 'string' }, description: 'Server IDs to disable.' },
            keep_only: { type: 'array', items: { type: 'string' }, description: 'Whitelist: keep only these server IDs enabled (hotplug_control is always preserved).' }
          },
          additionalProperties: false
        },
        tools: {
          type: 'array',
          description: 'Optional per-server category/tool changes (applied in one atomic batch).',
          items: {
            type: 'object',
            properties: {
              server_id: { type: 'string', description: 'Target MCP server ID.' },
              reset_all: { type: 'string', enum: ['enable', 'disable'], description: 'Enable or disable all tools on this server before other operations.' },
              keep_only_categories: { type: 'array', items: { type: 'string' }, description: 'Whitelist exact full category/toolset names to keep enabled.' },
              enable_categories: { type: 'array', items: { type: 'string' }, description: 'Exact full category/toolset names to enable.' },
              disable_categories: { type: 'array', items: { type: 'string' }, description: 'Exact full category/toolset names to disable.' },
              keep_only_tools: {
                description: 'Whitelist exact full tool names: either string[] (for whole server) or { "<exact_category>": ["<exact_tool>", ...] }.'
              },
              enable_tools: { type: 'array', items: { type: 'string' }, description: 'Exact full tool names to enable.' },
              disable_tools: { type: 'array', items: { type: 'string' }, description: 'Exact full tool names to disable.' }
            },
            required: ['server_id'],
            additionalProperties: false
          }
        }
      },
      additionalProperties: false
    }
  }
];

function isPlainObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function normalizeStringArray(value) {
  if (!Array.isArray(value)) return [];
  const seen = new Set();
  const out = [];
  for (const raw of value) {
    if (typeof raw !== 'string') continue;
    const trimmed = raw.trim();
    if (!trimmed || seen.has(trimmed)) continue;
    seen.add(trimmed);
    out.push(trimmed);
  }
  return out;
}

async function handleListTools(args, deps) {
  const { stateCache, waitForLoadingServers } = deps;
  const serverId = typeof args.server_id === 'string' ? args.server_id.trim() : '';
  const statusFilter = args.status === 'enabled' || args.status === 'disabled' ? args.status : 'all';
  const search = typeof args.search === 'string' ? args.search.trim() : '';
  const categories = normalizeStringArray(args.categories);

  if (serverId) {
    await waitForLoadingServers(15000, serverId);
    const tree = stateCache.getServerToolTree(serverId);
    if (!tree) {
      const known = stateCache.getAllServers().map(s => s.serverId).sort();
      throw new Error('Unknown server_id "' + serverId + '". Available servers: ' + known.join(', '));
    }
    const inspected = toolPolicy.inspectServerToolTree(tree, {
      categories,
      status: statusFilter,
      search
    });
    return {
      server_id: tree.serverId,
      name: tree.name,
      enabled: tree.enabled,
      status: tree.health,
      enabled_tools_count: tree.enabledLeafCount,
      total_tools_count: tree.totalLeafCount,
      ...inspected
    };
  }

  await waitForLoadingServers(15000, null);
  const allServers = stateCache.getAllServers().sort((a, b) => a.serverId.localeCompare(b.serverId));

  if (search) {
    const matches = [];
    for (const srv of allServers) {
      const tree = stateCache.getServerToolTree(srv.serverId);
      if (!tree) continue;
      const inspected = toolPolicy.inspectServerToolTree(tree, { status: statusFilter, search });
      const hasCats = Array.isArray(inspected.matched_categories) && inspected.matched_categories.length > 0;
      const hasEnabled = Array.isArray(inspected.enabled_tools) && inspected.enabled_tools.length > 0;
      const hasDisabled = Array.isArray(inspected.disabled_tools) && inspected.disabled_tools.length > 0;
      if (hasCats || hasEnabled || hasDisabled) {
        matches.push({
          server_id: srv.serverId,
          name: srv.name,
          enabled: srv.enabled,
          status: srv.health,
          ...inspected
        });
      }
    }
    return { search, status: statusFilter, matches };
  }

  const servers = [];
  for (const srv of allServers) {
    if (statusFilter === 'enabled' && !srv.enabled) continue;
    if (statusFilter === 'disabled' && srv.enabled) continue;
    const tree = stateCache.getServerToolTree(srv.serverId);
    const overview = tree ? toolPolicy.inspectServerToolTree(tree, { status: 'all' }) : null;
    const entry = {
      server_id: srv.serverId,
      name: srv.name,
      enabled: srv.enabled,
      status: srv.health,
      enabled_tools: tree ? tree.enabledLeafCount : srv.enabledToolsCount,
      total_tools: tree ? tree.totalLeafCount : srv.totalToolsCount,
      has_categories: Boolean(overview && overview.has_categories)
    };
    if (overview && overview.has_categories && Array.isArray(overview.categories)) {
      entry.categories_count = overview.categories.length;
    }
    servers.push(entry);
  }
  return { servers };
}

async function handleManageTools(args, deps) {
  const { stateCache, waitForLoadingServers, dispatchControlCommand } = deps;
  const allServers = stateCache.getAllServers();
  const serverById = new Map(allServers.map(s => [s.serverId, s]));

  const serverChanges = {};
  const toolPolicyChanges = {};
  const toolsSummary = [];
  const notFound = [];
  const warnings = [];

  if (isPlainObject(args.servers)) {
    const srvArgs = args.servers;
    const desiredEnabled = new Map(allServers.map(s => [s.serverId, Boolean(s.enabled)]));

    if (Array.isArray(srvArgs.keep_only)) {
      const keepList = normalizeStringArray(srvArgs.keep_only);
      const validKeep = [];
      for (const id of keepList) {
        if (serverById.has(id)) validKeep.push(id);
        else notFound.push('server:' + id);
      }
      if (validKeep.length > 0 || keepList.length === 0) {
        const keepSet = new Set(validKeep);
        keepSet.add(toolPolicy.HOTPLUG_CONTROL_SERVER_ID);
        for (const id of desiredEnabled.keys()) {
          desiredEnabled.set(id, keepSet.has(id));
        }
      }
    }

    for (const id of normalizeStringArray(srvArgs.disable)) {
      if (id === toolPolicy.HOTPLUG_CONTROL_SERVER_ID) {
        warnings.push('hotplug_control cannot disable itself; toggle it in the MCP Hotplug UI.');
        continue;
      }
      if (!serverById.has(id)) {
        notFound.push('server:' + id);
        continue;
      }
      desiredEnabled.set(id, false);
    }

    for (const id of normalizeStringArray(srvArgs.enable)) {
      if (!serverById.has(id)) {
        notFound.push('server:' + id);
        continue;
      }
      desiredEnabled.set(id, true);
    }

    for (const [id, nextEnabled] of desiredEnabled.entries()) {
      const current = serverById.get(id);
      if (current && Boolean(current.enabled) !== nextEnabled) {
        serverChanges[id] = nextEnabled;
      }
    }
  }

  if (Array.isArray(args.tools)) {
    for (const entry of args.tools) {
      if (!isPlainObject(entry)) continue;
      const serverId = typeof entry.server_id === 'string' ? entry.server_id.trim() : '';
      if (!serverId) continue;

      if (serverId === toolPolicy.HOTPLUG_CONTROL_SERVER_ID) {
        warnings.push('hotplug_control tools cannot be modified by manage_tools; configure them in the MCP Hotplug UI.');
        continue;
      }

      if (!serverById.has(serverId)) {
        notFound.push('server:' + serverId);
        continue;
      }

      await waitForLoadingServers(15000, serverId);
      const tree = stateCache.getServerToolTree(serverId);
      if (!tree || (!Array.isArray(tree.nodes) || tree.nodes.length === 0)) {
        warnings.push('No cached tool tree available yet for server "' + serverId + '". Enable/connect it once first.');
        continue;
      }

      const mutationResult = toolPolicy.applyServerToolMutations(tree, entry);
      for (const missing of mutationResult.notFound) {
        notFound.push(serverId + ':' + missing);
      }

      const currentNormalized = toolPolicy.normalizeDisabledList(tree.disabledTools || []);
      const nextNormalized = mutationResult.disabledTools;
      if (JSON.stringify(currentNormalized) !== JSON.stringify(nextNormalized)) {
        toolPolicyChanges[serverId] = nextNormalized;
      }
      toolsSummary.push({
        server_id: serverId,
        enabled_tools: mutationResult.enabledCount,
        total_tools: mutationResult.totalCount,
        changed_tools: mutationResult.changedCount
      });
    }
  }

  const hasServerChanges = Object.keys(serverChanges).length > 0;
  const hasToolChanges = Object.keys(toolPolicyChanges).length > 0;

  if (!hasServerChanges && !hasToolChanges) {
    const catalog = stateCache.buildCatalog();
    return {
      applied: false,
      message: 'No state changes were required.',
      catalog_revision: catalog.catalog_revision,
      ...(toolsSummary.length > 0 ? { tools: toolsSummary } : {}),
      ...(notFound.length > 0 ? { not_found: [...new Set(notFound)] } : {}),
      ...(warnings.length > 0 ? { warnings } : {})
    };
  }

  const dispatchRes = await dispatchControlCommand({
    serverChanges,
    toolPolicyChanges
  });
  if (dispatchRes && dispatchRes.ok === false) {
    throw new Error(dispatchRes.error || 'Failed to apply Hotplug control changes.');
  }
  if (dispatchRes && Array.isArray(dispatchRes.warnings)) {
    warnings.push(...dispatchRes.warnings);
  }

  const freshCatalog = stateCache.buildCatalog();
  return {
    applied: true,
    catalog_revision: freshCatalog.catalog_revision,
    ...(hasServerChanges ? { servers_changed: serverChanges } : {}),
    ...(toolsSummary.length > 0 ? { tools_changed: toolsSummary } : {}),
    ...(notFound.length > 0 ? { not_found: [...new Set(notFound)] } : {}),
    ...(warnings.length > 0 ? { warnings } : {})
  };
}

function sendJson(res, statusCode, payload, headers = {}) {
  res.writeHead(statusCode, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
    ...headers
  });
  res.end(JSON.stringify(payload));
}

async function handleControlMcpRequest(req, res, deps) {
  if (req.method === 'DELETE') {
    sendJson(res, 200, { ok: true });
    return;
  }
  if (req.method === 'GET') {
    res.writeHead(405, { Allow: 'POST, DELETE', 'Cache-Control': 'no-store' });
    res.end();
    return;
  }
  if (req.method !== 'POST') {
    res.writeHead(405, { Allow: 'POST, DELETE' });
    res.end();
    return;
  }

  const rawBody = await deps.readRequestBody(req);
  let rpc;
  try {
    rpc = JSON.parse(rawBody);
  } catch (_) {
    sendJson(res, 400, { jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Parse error.' } });
    return;
  }

  if (!rpc || rpc.jsonrpc !== '2.0' || typeof rpc.method !== 'string') {
    sendJson(res, 400, {
      jsonrpc: '2.0',
      id: rpc && rpc.id !== undefined ? rpc.id : null,
      error: { code: -32600, message: 'Invalid Request.' }
    });
    return;
  }

  if (rpc.id === undefined || rpc.id === null) {
    res.writeHead(202, { 'Cache-Control': 'no-store' });
    res.end();
    return;
  }

  const sessionId = String(req.headers['mcp-session-id'] || 'hotplug-control-session');
  let result;

  if (rpc.method === 'initialize') {
    result = {
      protocolVersion: PROTOCOL_VERSION,
      capabilities: { tools: {} },
      serverInfo: SERVER_INFO
    };
  } else if (rpc.method === 'ping') {
    result = {};
  } else if (rpc.method === 'tools/list') {
    result = { tools: TOOLS };
  } else if (rpc.method === 'tools/call') {
    const params = isPlainObject(rpc.params) ? rpc.params : {};
    const name = String(params.name || '');
    const args = isPlainObject(params.arguments) ? params.arguments : {};
    try {
      let data;
      if (name === toolPolicy.HOTPLUG_CONTROL_LIST_TOOL) {
        data = await handleListTools(args, deps);
      } else if (name === toolPolicy.HOTPLUG_CONTROL_MANAGE_TOOL) {
        data = await handleManageTools(args, deps);
      } else {
        result = {
          isError: true,
          content: [{ type: 'text', text: 'Unknown Hotplug Control MCP tool: ' + name }]
        };
      }
      if (!result) {
        result = {
          content: [{ type: 'text', text: JSON.stringify(data) }],
          structuredContent: data
        };
      }
    } catch (err) {
      result = {
        isError: true,
        content: [{ type: 'text', text: err instanceof Error ? err.message : String(err) }]
      };
    }
  } else {
    sendJson(res, 200, {
      jsonrpc: '2.0',
      id: rpc.id,
      error: { code: -32601, message: 'Method not found: ' + rpc.method }
    }, { 'Mcp-Session-Id': sessionId });
    return;
  }

  sendJson(res, 200, { jsonrpc: '2.0', id: rpc.id, result }, { 'Mcp-Session-Id': sessionId });
}

module.exports = {
  TOOLS,
  handleControlMcpRequest
};
