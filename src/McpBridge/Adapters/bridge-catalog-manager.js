'use strict';

const crypto = require('node:crypto');

const SOURCE_META_KEY = 'com.mcpbridge/source';

function safeObject(value) {
  return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
}

function encodeToolId(serverId, toolName) {
  return encodeURIComponent(serverId) + '::' + encodeURIComponent(toolName);
}

class ToolCatalogManager {
  constructor(routerClient) {
    this.routerClient = routerClient;
    this.revision = 0;
    this.fingerprint = '';
    this.toolIndex = new Map();
    this.lastSnapshot = null;
  }

  async getCatalog() {
    try {
      // This endpoint serializes only the Router's already-published state cache.
      const result = await this.routerClient.getCachedCatalog();
      if (!Array.isArray(result.tools) || !Array.isArray(result.servers))
        throw new Error('Internal router cache API returned an invalid catalog.');

      const statusSource = result.servers.map(item => ({
        id: String(item.serverId || item.id || ''),
        name: String(item.name || item.serverId || item.id || ''),
        enabled: item.enabled !== false,
        pending: item.pending === true,
        scope: String(item.scope || 'local'),
        transport: String(item.transport || 'http'),
        status: String(item.health || item.status || 'Unknown'),
        is_polling: Boolean(item.isPolling),
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
      })).filter(item => item.id);
      const configuredServers = new Map(statusSource.map(item => [item.id, item]));
      const servers = new Map();

      for (const item of statusSource) {
        servers.set(item.id, {
          id: item.id,
          name: item.name,
          enabled: item.enabled,
          pending: item.pending,
          scope: item.scope,
          transport: item.transport,
          status: item.status,
          is_polling: item.is_polling,
          poll_interval_ms: item.poll_interval_ms,
          tools_count: item.tools_count,
          stale: item.stale,
          last_attempt_at: item.last_attempt_at,
          last_completed_at: item.last_completed_at,
          last_success_at: item.last_success_at,
          response_time_ms: item.response_time_ms,
          consecutive_failures: item.consecutive_failures,
          last_error: item.last_error
        });
      }

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
          source: {
            server_id: serverId,
            server_name: serverName
          }
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
            config_revision: configured ? configured.config_revision : 0,
            server_revision: configured ? configured.server_revision : 0,
            catalog_revision: configured ? configured.catalog_revision : 0,
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

      const snapshot = {
        catalog_revision: String(result.catalog_revision || this.revision),
        cache_revision: String(result.cache_revision || ''),
        status: 'ready',
        generated_at: new Date().toISOString(),
        servers: serverList,
        tools
      };
      this.toolIndex = toolIndex;
      this.lastSnapshot = snapshot;
      return { snapshot, toolIndex };
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      const snapshot = this.lastSnapshot
        ? JSON.parse(JSON.stringify(this.lastSnapshot))
        : {
          catalog_revision: String(this.revision),
          cache_revision: '',
          status: 'stale',
          generated_at: new Date().toISOString(),
          servers: [],
          tools: []
        };
      snapshot.status = 'stale';
      snapshot.generated_at = new Date().toISOString();
      snapshot.refresh_error = message;
      return {
        snapshot,
        toolIndex: this.toolIndex
      };
    }
  }

  getCachedDescriptor(toolId) {
    return this.toolIndex.get(String(toolId)) || null;
  }

  async getCurrentServerToolDescriptor(descriptor) {
    const current = await this.routerClient.getServerCache(descriptor.serverId);
    const state = current && current.state;
    if (!state || state.enabled === false || !Array.isArray(current.tools))
      return null;

    const tool = current.tools.find(item =>
      item && typeof item.name === 'string' && item.name === descriptor.originalName
    );
    if (!tool) return null;

    return {
      ...descriptor,
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