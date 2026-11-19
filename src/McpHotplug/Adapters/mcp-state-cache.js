'use strict';

const crypto = require('node:crypto');
const { EventEmitter } = require('node:events');
const toolPolicy = require('./mcp-tool-policy');

function cloneJson(value) {
  if (value === undefined) return undefined;
  return JSON.parse(JSON.stringify(value));
}

function stableValue(value) {
  if (Array.isArray(value)) return value.map(stableValue);
  if (value && typeof value === 'object') {
    const result = {};
    for (const key of Object.keys(value).sort())
      result[key] = stableValue(value[key]);
    return result;
  }
  return value;
}

function fingerprint(value) {
  return crypto.createHash('sha256').update(JSON.stringify(stableValue(value))).digest('hex');
}

function transportFingerprint(config) {
  const transportConfig = { ...config };
  delete transportConfig.name;
  delete transportConfig.enabled;
  delete transportConfig.scope;
  delete transportConfig.pollIntervalMs;
  delete transportConfig.temporary;
  delete transportConfig.localTargetOffline;
  delete transportConfig.disabledTools;
  return fingerprint(transportConfig);
}

function safeError(value) {
  let message = value instanceof Error ? value.message : String(value || 'Unknown MCP error');
  message = message.replace(/Bearer\s+[^\s,;]+/gi, 'Bearer [REDACTED]')
    .replace(/github_pat_[A-Za-z0-9_]+/gi, '[REDACTED]')
    .replace(/\bgh[pousr]_[A-Za-z0-9_]+\b/gi, '[REDACTED]')
    .replace(/([?&](?:access_token|token|api[_-]?key|secret|authorization|password)=)[^&\s]+/gi, '$1[REDACTED]')
    .replace(/[\r\n\t]+/g, ' ');
  return message.length > 520 ? message.slice(0, 517) + '...' : message;
}

class McpServerStateCache extends EventEmitter {
  constructor() {
    super();
    this.states = new Map();
    this.cacheRevision = 0;
    this.catalogRevision = 0;
  }

  registerServer(config, configRevision) {
    const id = String(config.id);
    const existing = this.states.get(id);
    const nextTransportFingerprint = transportFingerprint(config);
    const transportChanged = Boolean(
      existing && existing.transportFingerprint !== nextTransportFingerprint
    );
    const name = String(config.name || id);
    const enabled = config.enabled !== false;
    const pending = config.pending === true;
    const scope = config.scope === 'remote' ? 'remote' : 'local';
    const transport = String(config.transport || 'http');
    const pollIntervalMs = Number(config.pollIntervalMs) > 0
      ? Number(config.pollIntervalMs)
      : (scope === 'remote' ? 30000 : 2500);
    const nextDisabledTools = Array.isArray(config.disabledTools)
      ? toolPolicy.normalizeDisabledList(config.disabledTools)
      : (existing ? existing.disabledTools : []);
    const disabledToolsChanged = Boolean(
      existing && JSON.stringify(existing.disabledTools) !== JSON.stringify(nextDisabledTools)
    );
    const reenabled = Boolean(existing && existing.enabled === false && enabled);
    const enabledChanged = Boolean(existing && existing.enabled !== enabled);
    const routeMetadataChanged = Boolean(
      !existing ||
      transportChanged ||
      enabledChanged ||
      disabledToolsChanged ||
      existing.name !== name
    );
    const metadataChanged = Boolean(
      routeMetadataChanged ||
      (existing && existing.pollIntervalMs !== pollIntervalMs)
    );
    const declaredConfigurationFingerprint =
      typeof config.configurationFingerprint === 'string'
        ? config.configurationFingerprint
        : null;
    const sameDeclaredConfiguration = Boolean(
      existing &&
      declaredConfigurationFingerprint &&
      existing.configurationFingerprint === declaredConfigurationFingerprint
    );
    const preserveCatalog = Boolean(
      existing &&
      existing.tools.length > 0 &&
      sameDeclaredConfiguration &&
      (pending || existing.pending)
    );
    const clearTransportState = transportChanged && !preserveCatalog;

    let nextCatalogRevision = clearTransportState ? 0 : (existing ? existing.catalogRevision : 0);
    if (disabledToolsChanged) {
      nextCatalogRevision += 1;
      this.catalogRevision += 1;
    }

    const next = {
      serverId: id,
      name,
      enabled,
      pending,
      scope,
      transport,
      temporary: config.temporary === true,
      configurationFingerprint: declaredConfigurationFingerprint,
      health: !enabled
        ? 'disabled'
        : (pending || transportChanged || reenabled
          ? 'waiting'
          : (existing ? existing.health : 'waiting')),
      isPolling: existing && !transportChanged ? existing.isPolling : false,
      isDiscovering: existing && !transportChanged ? Boolean(existing.isDiscovering) : false,
      pollIntervalMs,
      configRevision: existing
        ? (transportChanged
          ? Math.max(Number(configRevision) || 0, existing.configRevision + 1)
          : existing.configRevision)
        : (Number(configRevision) || 1),
      transportFingerprint: nextTransportFingerprint,
      serverRevision: existing
        ? existing.serverRevision + (routeMetadataChanged ? 1 : 0)
        : 1,
      serverRevisionToken: !existing || routeMetadataChanged
        ? crypto.randomUUID()
        : existing.serverRevisionToken,
      catalogRevision: nextCatalogRevision,
      lastAttemptAt: clearTransportState ? null : (existing ? existing.lastAttemptAt : null),
      lastCompletedAt: clearTransportState ? null : (existing ? existing.lastCompletedAt : null),
      lastSuccessAt: clearTransportState ? null : (existing ? existing.lastSuccessAt : null),
      responseTimeMs: clearTransportState ? null : (existing ? existing.responseTimeMs : null),
      consecutiveFailures: clearTransportState || reenabled
        ? 0
        : (existing ? existing.consecutiveFailures : 0),
      stale: preserveCatalog || (existing && !transportChanged)
        ? Boolean(existing && existing.stale) || preserveCatalog || (reenabled && existing.tools.length > 0)
        : false,
      lastError: clearTransportState || reenabled
        ? null
        : (existing ? existing.lastError : null),
      tools: clearTransportState ? [] : (existing ? existing.tools : []),
      persistedTools: clearTransportState ? [] : (existing ? existing.persistedTools : []),
      discoveredNested: clearTransportState ? null : (existing ? existing.discoveredNested : null),
      disabledTools: nextDisabledTools,
      toolFingerprint: clearTransportState ? '' : (existing ? existing.toolFingerprint : ''),
      serverInfo: clearTransportState ? null : (existing ? existing.serverInfo : null),
      protocolMode: clearTransportState ? null : (existing ? existing.protocolMode : null),
      needsDownload: config.needsDownload === true,
      firstDownloadStartedAt: existing && existing.firstDownloadStartedAt ? existing.firstDownloadStartedAt : Date.now(),
      updatedAt: new Date().toISOString()
    };
    this.states.set(id, next);
    if (metadataChanged)
      this._publish('server.configured', id, { catalogChanged: disabledToolsChanged || enabledChanged });
    return this.getServer(id);
  }

  hydratePersistedTree(id, persistedTools, discoveredNested) {
    const state = this.states.get(String(id));
    if (!state) return false;
    let changed = false;
    if (Array.isArray(persistedTools) && persistedTools.length > 0 && state.persistedTools.length === 0) {
      state.persistedTools = cloneJson(persistedTools);
      changed = true;
    }
    if (discoveredNested && typeof discoveredNested === 'object' && !state.discoveredNested) {
      state.discoveredNested = cloneJson(discoveredNested);
      changed = true;
    }
    if (changed) {
      state.updatedAt = new Date().toISOString();
      this._publish('server.nestedDiscovered', state.serverId, { catalogChanged: true });
    }
    return changed;
  }

  setDisabledTools(id, disabledList) {
    const state = this.states.get(String(id));
    if (!state) return null;
    const normalized = toolPolicy.normalizeDisabledList(disabledList);
    if (JSON.stringify(state.disabledTools) === JSON.stringify(normalized)) {
      return this.getServer(id);
    }
    state.disabledTools = normalized;
    state.catalogRevision += 1;
    this.catalogRevision += 1;
    state.serverRevision += 1;
    state.serverRevisionToken = crypto.randomUUID();
    state.updatedAt = new Date().toISOString();
    this._publish('server.policyChanged', state.serverId, { catalogChanged: true });
    return this.getServer(id);
  }

  setDiscovering(id, isDiscovering) {
    const state = this.states.get(String(id));
    if (!state) return false;
    const next = Boolean(isDiscovering);
    if (state.isDiscovering === next) return false;
    state.isDiscovering = next;
    state.updatedAt = new Date().toISOString();
    this._publish('server.nestedDiscovering', state.serverId, { isDiscovering: next });
    return true;
  }

  setDiscoveredNested(id, discoveredNested) {
    const state = this.states.get(String(id));
    if (!state) return false;
    state.discoveredNested = discoveredNested ? cloneJson(discoveredNested) : null;
    state.isDiscovering = false;
    state.catalogRevision += 1;
    this.catalogRevision += 1;
    state.serverRevision += 1;
    state.serverRevisionToken = crypto.randomUUID();
    state.updatedAt = new Date().toISOString();
    this._publish('server.nestedDiscovered', state.serverId, { catalogChanged: true });
    return true;
  }

  setPolling(id, isPolling, attemptedAt = null) {
    const state = this.states.get(String(id));
    if (!state) return false;
    const prevPolling = state.isPolling;
    state.isPolling = Boolean(isPolling);
    if (attemptedAt) state.lastAttemptAt = attemptedAt;
    state.updatedAt = new Date().toISOString();
    if (state.health !== 'waiting' && prevPolling !== state.isPolling) {
      this._publish('server.polling', state.serverId);
    }
    return true;
  }

  setSuccess(id, result) {
    const state = this.states.get(String(id));
    if (!state) return false;
    const tools = Array.isArray(result.tools) ? cloneJson(result.tools) : [];
    const nextFingerprint = fingerprint(tools);
    const catalogChanged = nextFingerprint !== state.toolFingerprint;
    const nextProtocolMode = result.protocolMode || null;
    const protocolChanged = state.protocolMode !== nextProtocolMode;
    if (catalogChanged) {
      state.catalogRevision += 1;
      this.catalogRevision += 1;
    }
    state.tools = tools;
    if (tools.length > 0) state.persistedTools = cloneJson(tools);
    state.toolFingerprint = nextFingerprint;
    state.health = 'online';
    state.pending = false;
    state.isPolling = false;
    state.lastCompletedAt = result.completedAt || new Date().toISOString();
    state.lastSuccessAt = state.lastCompletedAt;
    state.responseTimeMs = Number.isFinite(result.responseTimeMs) ? result.responseTimeMs : null;
    state.consecutiveFailures = 0;
    state.stale = false;
    state.lastError = null;
    state.serverInfo = result.serverInfo ? cloneJson(result.serverInfo) : state.serverInfo;
    state.protocolMode = nextProtocolMode;
    state.updatedAt = new Date().toISOString();
    this._publish('verification.completed', state.serverId, {
      success: true,
      configRevision: state.configRevision,
      catalogRevision: state.catalogRevision,
      catalogChanged,
      protocolChanged,
      toolsCount: state.tools.length,
      completedAt: state.lastCompletedAt,
      responseTimeMs: state.responseTimeMs
    });
    return true;
  }

  setPending(id, message, result = {}) {
    const state = this.states.get(String(id));
    if (!state) return false;
    state.pending = true;
    state.health = !state.enabled ? 'disabled' : (state.needsDownload ? 'downloading' : 'waiting');
    state.isPolling = false;
    state.lastCompletedAt = result.completedAt || new Date().toISOString();
    state.responseTimeMs = Number.isFinite(result.responseTimeMs) ? result.responseTimeMs : null;
    state.lastError = null;
    state.updatedAt = new Date().toISOString();
    this._publish('verification.completed', state.serverId, {
      success: false,
      pending: true,
      configRevision: state.configRevision,
      catalogRevision: state.catalogRevision,
      catalogChanged: false,
      toolsCount: state.tools.length,
      stale: state.stale,
      completedAt: state.lastCompletedAt,
      responseTimeMs: state.responseTimeMs
    });
    return true;
  }

  setFailure(id, error, result = {}) {
    const state = this.states.get(String(id));
    if (!state) return false;

    if (error && Array.isArray(error.discoveredTools) && error.discoveredTools.length > 0 && state.tools.length === 0) {
      state.tools = cloneJson(error.discoveredTools);
      state.persistedTools = cloneJson(error.discoveredTools);
      state.toolFingerprint = fingerprint(state.tools);
    }
    if (error && error.discoveredServerInfo && !state.serverInfo) {
      state.serverInfo = cloneJson(error.discoveredServerInfo);
    }

    state.consecutiveFailures += 1;
    state.pending = false;
    state.health = !state.enabled ? 'disabled' : 'offline';
    state.isPolling = false;
    state.lastCompletedAt = result.completedAt || new Date().toISOString();
    state.responseTimeMs = Number.isFinite(result.responseTimeMs) ? result.responseTimeMs : null;
    state.stale = state.tools.length > 0;
    state.lastError = safeError(error);
    state.updatedAt = new Date().toISOString();

    this._publish('verification.completed', state.serverId, {
      success: false,
      configRevision: state.configRevision,
      catalogRevision: state.catalogRevision,
      catalogChanged: false,
      toolsCount: state.tools.length,
      stale: state.stale,
      error: state.lastError,
      completedAt: state.lastCompletedAt,
      responseTimeMs: state.responseTimeMs
    });
    return true;
  }

  setDisabled(id) {
    const state = this.states.get(String(id));
    if (!state) return false;
    const changed = state.enabled !== false ||
      state.health !== 'disabled' ||
      state.isPolling ||
      state.stale !== (state.tools.length > 0);
    state.enabled = false;
    state.health = 'disabled';
    state.isPolling = false;
    state.stale = state.tools.length > 0;
    if (changed) {
      state.updatedAt = new Date().toISOString();
      this._publish('server.disabled', state.serverId);
    }
    return true;
  }

  promoteServer(temporaryId, permanentConfig, configRevision) {
    const oldId = String(temporaryId);
    const newId = String(permanentConfig.id || '');
    const previous = this.states.get(oldId);
    if (!previous || !previous.temporary)
      throw new Error('Temporary MCP cache entry was not found.');
    if (!newId || newId === oldId)
      throw new Error('A distinct permanent MCP server id is required.');
    if (this.states.has(newId))
      throw new Error('A cache entry already exists for the permanent MCP server id.');

    const promoted = {
      ...previous,
      serverId: newId,
      name: String(permanentConfig.name || newId),
      enabled: permanentConfig.enabled !== false,
      scope: permanentConfig.scope === 'remote' ? 'remote' : 'local',
      pollIntervalMs: Number(permanentConfig.pollIntervalMs) > 0 ? Number(permanentConfig.pollIntervalMs) : (permanentConfig.scope === 'remote' ? 30000 : 2500),
      transport: String(permanentConfig.transport || 'http'),
      temporary: false,
      configRevision: Number(configRevision) || (previous.configRevision + 1),
      transportFingerprint: transportFingerprint(permanentConfig),
      configurationFingerprint: typeof permanentConfig.configurationFingerprint === 'string'
        ? permanentConfig.configurationFingerprint
        : null,
      disabledTools: Array.isArray(permanentConfig.disabledTools)
        ? toolPolicy.normalizeDisabledList(permanentConfig.disabledTools)
        : (previous.disabledTools || []),
      serverRevision: previous.serverRevision + 1,
      serverRevisionToken: crypto.randomUUID(),
      firstDownloadStartedAt: previous && previous.firstDownloadStartedAt ? previous.firstDownloadStartedAt : Date.now(),
      updatedAt: new Date().toISOString()
    };

    this.states.delete(oldId);
    this.states.set(newId, promoted);
    this._publish('server.removed', oldId, { temporary: true, promotedTo: newId });
    this._publish('server.promoted', newId, {
      temporary: false,
      previousServerId: oldId,
      configRevision: promoted.configRevision,
      catalogRevision: promoted.catalogRevision
    });
    return this.getServer(newId);
  }

  removeServer(id) {
    const key = String(id);
    const existing = this.states.get(key);
    const removed = this.states.delete(key);
    if (removed) this._publish('server.removed', key, {
      temporary: Boolean(existing && existing.temporary)
    });
    return removed;
  }

  getServer(id) {
    const state = this.states.get(String(id));
    return state ? this._publicState(state) : null;
  }

  getAllServers() {
    return [...this.states.values()]
      .filter(state => !state.temporary)
      .map(state => this._publicState(state));
  }

  getInternalServer(id) {
    return this.states.get(String(id)) || null;
  }

  getServerToolTree(id) {
    const state = this.states.get(String(id));
    if (!state || state.temporary) return null;
    const effectiveTools = state.tools.length > 0 ? state.tools : state.persistedTools;
    const tree = toolPolicy.buildServerToolTree(effectiveTools, state.discoveredNested, state.disabledTools);
    const pub = this._publicState(state);
    return {
      serverId: state.serverId,
      name: state.name,
      enabled: state.enabled,
      health: pub.health,
      rawHealth: state.health,
      isDiscovering: Boolean(state.isDiscovering),
      allToolsDisabled: pub.allToolsDisabled,
      disabledTools: [...state.disabledTools],
      rawToolsCount: effectiveTools.length,
      ...tree
    };
  }

  buildCatalog() {
    const candidates = [...this.states.values()].filter(state =>
      !state.temporary && state.enabled && state.health !== 'disabled' && state.tools.length > 0
    );

    const activeEntries = [];
    const nameCounts = new Map();
    for (const state of candidates) {
      const filtered = toolPolicy.filterCatalogToolsForServer(
        state.tools,
        state.discoveredNested,
        state.disabledTools
      );
      if (filtered.allDisabled || filtered.tools.length === 0) continue;
      activeEntries.push({ state, tools: filtered.tools });
      for (const tool of filtered.tools) {
        if (tool && typeof tool.name === 'string' && tool.name.trim()) {
          nameCounts.set(tool.name, (nameCounts.get(tool.name) || 0) + 1);
        }
      }
    }

    const tools = [];
    for (const { state, tools: filteredTools } of activeEntries) {
      const prefix = String(state.serverId).replace(/[^A-Za-z0-9_-]/g, '_');
      for (const rawTool of filteredTools) {
        if (!rawTool || typeof rawTool.name !== 'string' || !rawTool.name.trim()) continue;
        const originalName = rawTool.name;
        const uniquePrefix = prefix + '_' +
          crypto.createHash('sha256').update(String(state.serverId)).digest('hex').slice(0, 8);
        const exposedName = (nameCounts.get(originalName) || 0) > 1
          ? uniquePrefix + '__' + originalName
          : originalName;
        const existingMeta = rawTool._meta && typeof rawTool._meta === 'object' && !Array.isArray(rawTool._meta)
          ? rawTool._meta
          : {};
        tools.push({
          ...cloneJson(rawTool),
          name: exposedName,
          description: '[' + state.name + '] ' + String(rawTool.description || ''),
          _meta: {
            ...existingMeta,
            'com.mcphotplug/source': {
              serverId: state.serverId,
              serverName: state.name,
              originalName,
              serverRevision: state.serverRevisionToken + ':' + String(state.catalogRevision),
              configRevision: state.serverRevision,
              catalogRevision: state.catalogRevision
            }
          }
        });
      }
    }

    const revisionPayload = [...this.states.values()]
      .filter(state => !state.temporary && state.enabled)
      .map(state => [
        state.serverId,
        state.serverRevisionToken,
        state.serverRevision,
        state.catalogRevision,
        state.disabledTools
      ])
      .sort((a, b) => String(a[0]).localeCompare(String(b[0])));
    const revision = fingerprint(revisionPayload);
    return {
      status: 'ready',
      cache_revision: String(this.cacheRevision),
      catalog_revision: revision,
      disabled_tools_note: toolPolicy.DISABLED_TOOLS_NOTE,
      servers: this.getAllServers(),
      tools
    };
  }

  _publicState(state) {
    const effectiveTools = state.tools.length > 0 ? state.tools : state.persistedTools;
    const tree = toolPolicy.buildServerToolTree(effectiveTools, state.discoveredNested, state.disabledTools);
    const totalToolsCount = tree.totalLeafCount > 0 ? tree.totalLeafCount : effectiveTools.length;
    const enabledToolsCount = tree.totalLeafCount > 0
      ? tree.enabledLeafCount
      : (tree.allDisabled ? 0 : effectiveTools.length);
    const allToolsDisabled = Boolean(state.enabled && tree.allDisabled && totalToolsCount > 0);
    const effectiveHealth = allToolsDisabled ? 'offline' : state.health;
    const disabledHierarchy = state.enabled ? toolPolicy.buildCompactDisabledHierarchy(tree) : [];

    return {
      serverId: state.serverId,
      name: state.name,
      enabled: state.enabled,
      pending: state.pending,
      scope: state.scope,
      transport: state.transport,
      health: effectiveHealth,
      rawHealth: state.health,
      allToolsDisabled,
      isPolling: state.isPolling,
      pollIntervalMs: state.pollIntervalMs,
      configRevision: state.configRevision,
      serverRevision: state.serverRevision,
      catalogRevision: state.catalogRevision,
      lastAttemptAt: state.lastAttemptAt,
      lastCompletedAt: state.lastCompletedAt,
      lastSuccessAt: state.lastSuccessAt,
      responseTimeMs: state.responseTimeMs,
      consecutiveFailures: state.consecutiveFailures,
      stale: state.stale,
      lastError: state.lastError,
      toolsCount: enabledToolsCount,
      enabledToolsCount,
      totalToolsCount,
      disabledTools: disabledHierarchy,
      toolsPreview: state.tools.slice(0, 6).map(tool => tool.name),
      updatedAt: state.updatedAt
    };
  }

  _publish(event, serverId, details = {}) {
    this.cacheRevision += 1;
    const state = this.states.get(String(serverId));
    this.emit('changed', {
      event,
      serverId,
      cacheRevision: this.cacheRevision,
      at: new Date().toISOString(),
      temporary: Boolean(state && state.temporary),
      ...details
    });
  }
}

module.exports = { McpServerStateCache, safeError };