'use strict';

const crypto = require('node:crypto');
const toolPolicy = require('./mcp-tool-policy');

const SOURCE_META_KEY = 'com.mcphotplug/source';

function safeObject(value) {
  return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
}

function encodeToolId(serverId, toolName) {
  return encodeURIComponent(serverId) + '::' + encodeURIComponent(toolName);
}

function collectTopLevelDisabledNames(disabledItems, outSet) {
  if (!Array.isArray(disabledItems)) return;
  for (const item of disabledItems) {
    if (typeof item === 'string' && item.trim()) {
      outSet.add(item.trim());
    } else if (item && typeof item === 'object' && !Array.isArray(item)) {
      for (const key of Object.keys(item)) {
        if (key && key.trim()) outSet.add(key.trim());
      }
    }
  }
}

class ToolCatalogManager {
  constructor(routerClient) {
    this.routerClient = routerClient;
    this.revision = 0;
    this.fingerprint = '';
    this.toolIndex = new Map();
    this.disabledToolIndex = new Map();
    this.allDisabledServers = new Map();
    this.canSelfManage = false;
    this.lastSnapshot = null;
  }

  async getCatalog() {
    try {
      const result = await this.routerClient.getCachedCatalog();
      if (!Array.isArray(result.tools) || !Array.isArray(result.servers))
        throw new Error('Internal router cache API returned an invalid catalog.');

      const disabledToolIndex = new Map();
      const allDisabledServers = new Map();

      const statusSource = result.servers.map(item => {
        const id = String(item.serverId || item.id || '');
        const name = String(item.name || id);
        const disabledTools = Array.isArray(item.disabledTools) ? item.disabledTools : [];
        const allToolsDisabled = item.allToolsDisabled === true;

        if (id && allToolsDisabled) {
          allDisabledServers.set(id, { serverId: id, serverName: name });
        }
        if (id && disabledTools.length > 0) {
          const topNames = new Set();
          collectTopLevelDisabledNames(disabledTools, topNames);
          for (const tName of topNames) {
            disabledToolIndex.set(encodeToolId(id, tName), {
              serverId: id,
              serverName: name,
              originalName: tName
            });
          }
        }

        const entry = {
          id,
          name,
          enabled: item.enabled !== false,
          pending: item.pending === true,
          scope: String(item.scope || 'local'),
          transport: String(item.transport || 'http'),
          status: String(item.health || item.status || 'Unknown'),
          is_polling: Boolean(item.isPolling),
          is_discovering: Boolean(item.isDiscovering),
          tools_load_state: String(item.toolsLoadState || 'not_loaded'),
          poll_interval_ms: Number(item.pollIntervalMs) || 0,
          tools_count: Number.isInteger(item.toolsCount)
            ? item.toolsCount
            : (Number.isInteger(item.tools_count) ? item.tools_count : 0),
          stale: Boolean(item.stale),
          last_attempt_at: item.lastAttemptAt || null,
          last_completed_at: item.lastCompletedAt || null,
          last_success_at: item.lastSuccessAt || null,
          response_time_ms: Number.isFinite(item.responseTimeMs) ? item.responseTimeMs : null,
          consecutive_failures: Number(item.consecutiveFailures) || 0,
          last_error: item.lastError || item.last_error || null
        };
        if (Number.isInteger(item.totalToolsCount)) entry.total_tools_count = item.totalToolsCount;
        if (allToolsDisabled) entry.all_tools_disabled = true;
        if (disabledTools.length > 0) entry.disabled_tools = disabledTools;
        return entry;
      }).filter(item => item.id);

      const configuredServers = new Map(statusSource.map(item => [item.id, item]));
      const servers = new Map();
      for (const item of statusSource) servers.set(item.id, { ...item });

      const toolIndex = new Map();
      const tools = [];
      for (const rawTool of result.tools) {
        if (!rawTool || typeof rawTool.name !== 'string' || !rawTool.name.trim()) continue;
        const source = safeObject(safeObject(rawTool._meta)[SOURCE_META_KEY]);
        const serverId = String(source.serverId || 'unknown');
        const serverName = String(source.serverName || serverId);
        const originalName = String(source.originalName || rawTool.name);
        const toolId = encodeToolId(serverId, originalName);

        const descriptor = {
          tool_id: toolId,
          name: originalName,
          description: String(rawTool.description || ''),
          inputSchema: safeObject(rawTool.inputSchema),
          source: { server_id: serverId, server_name: serverName }
        };
        if (rawTool.title) descriptor.title = String(rawTool.title);
        if (rawTool.outputSchema) descriptor.outputSchema = rawTool.outputSchema;
        if (rawTool.annotations) descriptor.annotations = rawTool.annotations;

        tools.push(descriptor);
        toolIndex.set(toolId, {
          toolId,
          serverId,
          serverName,
          originalName,
          inputSchema: descriptor.inputSchema
        });

        if (!servers.has(serverId)) {
          const configured = configuredServers.get(serverId);
          servers.set(serverId, {
            id: serverId,
            name: String((configured && configured.name) || serverName),
            enabled: configured ? configured.enabled : true,
            pending: Boolean(configured && configured.pending),
            scope: String((configured && configured.scope) || 'local'),
            transport: String((configured && configured.transport) || 'http'),
            status: String((configured && configured.status) || 'Unknown'),
            is_polling: Boolean(configured && configured.is_polling),
            poll_interval_ms: configured ? configured.poll_interval_ms : 0,
            tools_count: configured ? configured.tools_count : 0,
            stale: Boolean(configured && configured.stale),
            last_attempt_at: configured ? configured.last_attempt_at : null,
            last_completed_at: configured ? configured.last_completed_at : null,
            last_success_at: configured ? configured.last_success_at : null,
            response_time_ms: configured ? configured.response_time_ms : null,
            consecutive_failures: configured ? configured.consecutive_failures : 0,
            last_error: configured ? configured.last_error : null
          });
        }
      }

      tools.sort((a, b) =>
        a.source.server_id.localeCompare(b.source.server_id) || a.name.localeCompare(b.name));
      const serverList = [...servers.values()].sort((a, b) => a.id.localeCompare(b.id));
      const nextFingerprint = crypto.createHash('sha256')
        .update(JSON.stringify({ servers: serverList, tools }))
        .digest('hex');

      if (nextFingerprint !== this.fingerprint) {
        this.revision += 1;
        this.fingerprint = nextFingerprint;
      }

      const canSelfManage = result.can_self_manage === true;
      const snapshot = {
        catalog_revision: String(result.catalog_revision || this.revision),
        cache_revision: String(result.cache_revision || ''),
        status: 'ready',
        generated_at: new Date().toISOString(),
        disabled_tools_note: String(result.disabled_tools_note || toolPolicy.getDisabledToolsNote(canSelfManage)),
        servers: serverList,
        tools
      };
      this.toolIndex = toolIndex;
      this.disabledToolIndex = disabledToolIndex;
      this.allDisabledServers = allDisabledServers;
      this.canSelfManage = canSelfManage;
      this.lastSnapshot = snapshot;
      return { snapshot, toolIndex };
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      const fallbackNote = toolPolicy.getDisabledToolsNote(this.canSelfManage);
      const snapshot = this.lastSnapshot
        ? JSON.parse(JSON.stringify(this.lastSnapshot))
        : {
          catalog_revision: String(this.revision),
          cache_revision: '',
          status: 'stale',
          generated_at: new Date().toISOString(),
          disabled_tools_note: fallbackNote,
          servers: [],
          tools: []
        };
      snapshot.status = 'stale';
      snapshot.generated_at = new Date().toISOString();
      snapshot.disabled_tools_note = fallbackNote;
      snapshot.refresh_error = message;
      return { snapshot, toolIndex: this.toolIndex };
    }
  }

  getCachedDescriptor(toolId) {
    return this.toolIndex.get(String(toolId)) || null;
  }

  getDisabledDescriptor(toolId) {
    const key = String(toolId || '');
    if (this.disabledToolIndex.has(key)) return this.disabledToolIndex.get(key);
    const sep = key.indexOf('::');
    if (sep > 0) {
      let serverId = key.slice(0, sep);
      let originalName = key.slice(sep + 2);
      try { serverId = decodeURIComponent(serverId); } catch (_) {}
      try { originalName = decodeURIComponent(originalName); } catch (_) {}
      if (this.allDisabledServers.has(serverId)) {
        const srv = this.allDisabledServers.get(serverId);
        return { serverId: srv.serverId, serverName: srv.serverName, originalName };
      }
    }
    return null;
  }

  async getCurrentServerToolDescriptor(descriptor) {
    const current = await this.routerClient.getServerCache(descriptor.serverId);
    const state = current && current.state;
    if (!state || state.enabled === false || !Array.isArray(current.tools)) return null;
    if (current.allToolsDisabled === true) return { ...descriptor, disabled: true };

    const tool = current.tools.find(item =>
      item && typeof item.name === 'string' && item.name === descriptor.originalName
    );
    if (!tool) {
      const disabledTop = Array.isArray(current.disabledTopLevelTools) ? current.disabledTopLevelTools : [];
      if (disabledTop.includes(descriptor.originalName)) return { ...descriptor, disabled: true };
      return null;
    }

    return {
      ...descriptor,
      disabled: false,
      serverRevision: String(current.server_revision || ''),
      inputSchema: safeObject(tool.inputSchema),
      description: String(tool.description || ''),
      state
    };
  }

  async getSnapshot() {
    const catalog = await this.getCatalog();
    return catalog.snapshot;
  }
}

module.exports = { ToolCatalogManager };