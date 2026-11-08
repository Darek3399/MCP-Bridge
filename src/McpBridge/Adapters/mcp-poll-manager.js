'use strict';

const crypto = require('node:crypto');
const { EventEmitter } = require('node:events');

function cloneConfig(config) {
  return {
    ...config,
    temporary: config.temporary === true,
    headers: config.headers && typeof config.headers === 'object' ? { ...config.headers } : undefined,
    args: Array.isArray(config.args) ? [...config.args] : undefined,
    env: config.env && typeof config.env === 'object' ? { ...config.env } : undefined
  };
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

function configFingerprint(config) {
  return crypto.createHash('sha256')
    .update(JSON.stringify(stableValue(config)))
    .digest('hex');
}

function transportFingerprint(config) {
  const transportConfig = { ...config };
  delete transportConfig.name;
  delete transportConfig.enabled;
  delete transportConfig.scope;
  delete transportConfig.pollIntervalMs;
  delete transportConfig.temporary;
  return configFingerprint(transportConfig);
}

function isLoopbackUrl(url) {
  try {
    const parsed = new URL(url);
    return parsed.hostname === '127.0.0.1' ||
      parsed.hostname === 'localhost' ||
      parsed.hostname === '::1' ||
      parsed.hostname === '[::1]';
  } catch (_) {
    return true;
  }
}

class McpServerWorker extends EventEmitter {
  constructor(config, cache, verifyServer, invokeTool, options = {}) {
    super();
    this.config = cloneConfig(config);
    this.cache = cache;
    this.verifyServer = verifyServer;
    this.invokeTool = invokeTool;
    this.intervalMs = Number(config.pollIntervalMs) > 0
      ? Number(config.pollIntervalMs)
      : (config.scope === 'remote' ? 30000 : 2500);
    this.generation = 1;
    this.runSequence = 0;
    this.running = false;
    this.stopped = false;
    this.timer = null;
    this.currentPromise = null;
    this.configurationChangePromise = null;
    this.pendingRun = false;
    this.waiters = new Set();
    this.operationTail = Promise.resolve();
    this.startDelayMs = Math.max(0, Number(options.startDelayMs) || 0);
    this.failureWindowStartedAt = Date.now();
    this.getStaggeredDelay = typeof options.getStaggeredDelay === 'function' ? options.getStaggeredDelay : (d => d);
    this.onConfigurationChanged = typeof options.onConfigurationChanged === 'function'
      ? options.onConfigurationChanged
      : async () => {};
    const registered = this.cache.registerServer(this.config, this.generation);
    if (registered && Number.isInteger(registered.configRevision) &&
        registered.configRevision > this.generation) {
      this.generation = registered.configRevision;
    }
  }

  start() {
    if (this.stopped || this.timer || this.running) return;
    this._schedule(this.startDelayMs);
  }

  _schedule(delayMs) {
    if (this.stopped) return;
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      this.timer = null;
      if (this.stopped) return;
      void this._run('scheduled');
    }, Math.max(0, delayMs));
    if (this.timer.unref) this.timer.unref();
  }

  subscribeToVerification(signal = null) {
    if (this.stopped) return Promise.reject(new Error('The MCP worker has been stopped.'));
    if (signal && signal.aborted)
      return Promise.reject(Object.assign(new Error('Verification subscription was cancelled.'), { name: 'AbortError' }));

    const targetGeneration = this.generation;
    let waiter;
    const promise = new Promise((resolve, reject) => {
      waiter = {
        generation: targetGeneration,
        resolve,
        reject,
        signal,
        abortHandler: null
      };
      if (signal) {
        waiter.abortHandler = () => {
          this.waiters.delete(waiter);
          reject(Object.assign(new Error('Verification subscription was cancelled.'), { name: 'AbortError' }));
        };
        signal.addEventListener('abort', waiter.abortHandler, { once: true });
      }
      this.waiters.add(waiter);
    });

    if (!this.running && !this.configurationChangePromise) {
      if (this.timer) {
        clearTimeout(this.timer);
        this.timer = null;
      }
      this._schedule(this.intervalMs);
      void this._run('requested');
    } else {
      this.pendingRun = true;
    }
    return promise;
  }

  _settleWaiter(waiter, action, value) {
    this.waiters.delete(waiter);
    if (waiter.signal && waiter.abortHandler)
      waiter.signal.removeEventListener('abort', waiter.abortHandler);
    action(value);
  }

  async updateConfig(nextConfig) {
    if (this.stopped) throw new Error('The MCP worker has been stopped.');
    const previous = this.config;
    const next = cloneConfig(nextConfig);
    const previousFingerprint = configFingerprint(previous);
    const nextFingerprint = configFingerprint(next);
    if (previousFingerprint === nextFingerprint) return false;

    const connectionChanged =
      transportFingerprint(previous) !== transportFingerprint(next);
    const previousInterval = this.intervalMs;

    this.config = next;
    this.intervalMs = Number(next.pollIntervalMs) > 0
      ? Number(next.pollIntervalMs)
      : (next.scope === 'remote' ? 30000 : 2500);

    if (!connectionChanged) {
      // Display-name and cadence changes update the snapshot without tearing
      // down a healthy MCP session or invalidating its in-flight verification.
      this.cache.registerServer(this.config, this.generation);
      if (previousInterval !== this.intervalMs && this.timer) {
        this._schedule(this.intervalMs);
      }
      this.emit('configurationChanged', {
        previous,
        current: cloneConfig(next),
        generation: this.generation,
        connectionChanged: false
      });
      return true;
    }

    this.generation += 1;
    this.failureWindowStartedAt = Date.now();
    for (const waiter of this.waiters) waiter.generation = this.generation;
    this.cache.registerServer(this.config, this.generation);

    const change = {
      previous,
      current: cloneConfig(next),
      generation: this.generation,
      connectionChanged: true
    };

    // Stop the old cadence immediately. The next verification is not allowed
    // to start until the per-server transport reset has completed.
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    const hadActiveRun = this.running;
    if (hadActiveRun) this.pendingRun = true;

    // Block new tools/call dispatches until only this server's old transport
    // and session have been cleared. No call may pair new credentials with an
    // old MCP session.
    const transition = Promise.resolve().then(() => this.onConfigurationChanged(change));
    this.configurationChangePromise = transition;
    try {
      await transition;
    } finally {
      if (this.configurationChangePromise === transition)
        this.configurationChangePromise = null;
    }
    this.emit('configurationChanged', change);

    if (this.stopped) return true;

    // If an old run was active, its completion path owns the immediate
    // replacement run after awaiting this transition. Scheduling an immediate
    // timer here as well would occasionally start the new configuration twice.
    this._schedule(hadActiveRun ? this.intervalMs : 0);
    return true;
  }

  async callTool(toolName, args, extraHeaders = null) {
    const pendingConfigurationChange = this.configurationChangePromise;
    if (pendingConfigurationChange)
      await pendingConfigurationChange;

    const generation = this.generation;
    const config = cloneConfig(this.config);
    if (this.stopped || config.enabled === false)
      throw new Error('The MCP server is disabled or removed.');
    if (generation !== this.generation)
      throw new Error('The MCP server configuration changed before the tool call started.');

    // MCP request ids are independent. A long tools/call must not hold up this
    // worker's scheduled verification; the worker still owns both operations.
    let result;
    try {
      result = await this.invokeTool(config, toolName, args, extraHeaders);
    } catch (error) {
      if (!this.stopped && generation === this.generation && this.config.temporary !== true) {
        if (!this.failureWindowStartedAt) this.failureWindowStartedAt = Date.now();
        if (!this.running && !this.configurationChangePromise) {
          this._schedule(this.getStaggeredDelay(1000, config.id));
        } else {
          this.pendingRun = true;
        }
      }
      throw error;
    }
    if (this.stopped || generation !== this.generation)
      throw new Error('The MCP server configuration changed while the tool call was running.');

    return result;
  }

  _enqueueOperation(operation) {
    const current = this.operationTail.then(operation, operation);
    this.operationTail = current.then(() => undefined, () => undefined);
    return current;
  }

  promoteConfig(nextConfig) {
    if (this.stopped) throw new Error('The MCP worker has been stopped.');
    if (this.running) throw new Error('An MCP worker can only be promoted after verification has completed.');
    if (this.config.temporary !== true) throw new Error('Only a temporary MCP worker can be promoted.');
    if (!nextConfig || !nextConfig.id || nextConfig.enabled === false)
      throw new TypeError('An enabled permanent MCP server configuration is required.');

    this.generation += 1;
    this.failureWindowStartedAt = Date.now();
    this.config = cloneConfig({ ...nextConfig, temporary: false });
    this.intervalMs = Number(this.config.pollIntervalMs) > 0
      ? Number(this.config.pollIntervalMs)
      : (this.config.scope === 'remote' ? 30000 : 2500);
    this.pendingRun = false;
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    this._schedule(this.intervalMs);
    this.emit('promoted', {
      serverId: this.config.id,
      configRevision: this.generation
    });
    return this.generation;
  }

  async stop() {
    if (this.stopped) return;
    this.stopped = true;
    this.generation += 1;
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    for (const waiter of [...this.waiters])
      this._settleWaiter(waiter, waiter.reject, new Error('The MCP worker was stopped.'));
    this.waiters.clear();
    this.emit('stopped');
    this.removeAllListeners();
  }

  async _run(reason) {
    if (this.stopped) return;
    if (this.running) {
      if (reason === 'requested') this.pendingRun = true;
      return this.currentPromise;
    }

    this.running = true;
    this.pendingRun = false;
    const generation = this.generation;
    const config = cloneConfig(this.config);
    const runId = String(generation) + '-' + (++this.runSequence);
    const startedAt = new Date().toISOString();
    const started = Date.now();
    this.cache.setPolling(config.id, true, startedAt);
    this.emit('started', { serverId: config.id, runId, generation, reason, startedAt });

    this.currentPromise = (async () => {
      let result = null;
      let error = null;
      try {
        result = await this._enqueueOperation(() => {
          if (this.stopped || generation !== this.generation)
            throw new Error('The MCP worker configuration changed before verification started.');
          return this.verifyServer(config);
        });
      } catch (failure) {
        error = failure;
      }

      const completedAt = new Date().toISOString();
      const responseTimeMs = Date.now() - started;
      const isCurrent = !this.stopped && generation === this.generation;

      let pending = false;
      if (isCurrent) {
        if (error) {
          this.cache.setFailure(config.id, error, { completedAt, responseTimeMs });
        } else if (result && result.pending === true) {
          pending = true;
          this.cache.setPending(config.id, result.message, { completedAt, responseTimeMs });
        } else {
          this.cache.setSuccess(config.id, {
            ...result,
            completedAt,
            responseTimeMs: (result && Number.isFinite(result.measuredResponseTimeMs)) ? result.measuredResponseTimeMs : responseTimeMs
          });
        }

        const event = {
          serverId: config.id,
          runId,
          configRevision: generation,
          cacheRevision: String(this.cache.cacheRevision),
          success: !error && !pending,
          pending,
          completedAt
        };
        this.emit('completed', event);
        this._resolveWaiters(generation, event);
      }

      this.running = false;
      this.currentPromise = null;

      if (this.stopped) return;
      if (this.pendingRun || generation !== this.generation) {
        const reasonNext = this.generation !== generation ? 'configuration-updated' : 'requested';
        this.pendingRun = false;
        const transition = this.configurationChangePromise;
        if (transition) {
          try { await transition; }
          catch (_) { return; }
        }
        if (!this.stopped)
          void this._run(reasonNext);
        return;
      }
      if (this.config.temporary === true) {
        // Temporary verification workers are one-shot. Configured workers,
        // including transports that are not available yet, keep polling.
        if (this.timer) {
          clearTimeout(this.timer);
          this.timer = null;
        }
        return;
      }

      let nextDelay = this.intervalMs;
      if (error) {
        if (!this.failureWindowStartedAt) {
          this.failureWindowStartedAt = Date.now();
        }
        if (config.scope === 'local') {
          nextDelay = 1500;
        } else {
          const elapsedRetryMs = Date.now() - this.failureWindowStartedAt;
          if (elapsedRetryMs < 5 * 60 * 1000) {
            nextDelay = 3000;
          } else if (elapsedRetryMs < 10 * 60 * 1000) {
            nextDelay = 10000;
          } else {
            nextDelay = 30000;
          }
          nextDelay = this.getStaggeredDelay(nextDelay, config.id);
        }
      } else {
        this.failureWindowStartedAt = 0;
      }
      this._schedule(nextDelay);
    })();

    return this.currentPromise;
  }

  _resolveWaiters(generation, event) {
    for (const waiter of [...this.waiters]) {
      if (waiter.generation !== generation) continue;
      this._settleWaiter(waiter, waiter.resolve, { ...event });
    }
  }
}

class McpPollManager extends EventEmitter {
  constructor(options) {
    super();
    if (!options || !options.cache || typeof options.verifyServer !== 'function' ||
        typeof options.invokeTool !== 'function') {
      throw new TypeError('McpPollManager requires cache, verifyServer and invokeTool dependencies.');
    }
    this.cache = options.cache;
    this.verifyServer = options.verifyServer;
    this.invokeTool = options.invokeTool;
    this.onWorkerConfigurationChanged = options.onWorkerConfigurationChanged || (() => {});
    this.workers = new Map();
    this.started = false;
    this._nextRetrySlotAt = 0;
    this.configurationTail = Promise.resolve();
    this._cacheHandler = event => {
      if (event && event.event === 'server.polling') return;
      this.emit('cacheChanged', event);
    };
    this.cache.on('changed', this._cacheHandler);
  }

  replaceConfiguration(configs) {
    const update = this.configurationTail.then(
      () => this._replaceConfiguration(configs),
      () => this._replaceConfiguration(configs)
    );
    this.configurationTail = update.then(() => undefined, () => undefined);
    return update;
  }

  async _replaceConfiguration(configs) {
    const desired = new Map();
    for (const item of Array.isArray(configs) ? configs : []) {
      if (!item || !item.id ||
          (item.enabled !== false && item.pending !== true && !item.url)) continue;
      desired.set(String(item.id), cloneConfig(item));
    }

    const existingIds = new Set([
      ...this.workers.keys(),
      ...this.cache.getAllServers().map(state => state.serverId)
    ]);
    for (const id of existingIds) {
      const worker = this.workers.get(id);
      if ((!worker || worker.config.temporary !== true) && !desired.has(id))
        await this.removeServer(id);
    }

    await Promise.all(
      [...desired.values()].map((config, idx) => this.upsertServer(config, { startDelayMs: idx * 250 }))
    );
    this.started = true;
    return { updated: true, count: this.workers.size };
  }

  async upsertServer(config, options = {}) {
    if (!config || !config.id) throw new TypeError('MCP server id is required.');
    const id = String(config.id);
    if (config.enabled === false) {
      const worker = this.workers.get(id);
      if (worker) {
        this.workers.delete(id);
        try {
          await this._handleWorkerConfigChange({
            previous: cloneConfig(worker.config),
            current: cloneConfig(config),
            generation: worker.generation + 1,
            connectionChanged: true,
            disabled: true
          });
        } finally {
          await worker.stop();
        }
      }

      // Disabling a server removes its tools from the aggregate catalog, but
      // keeps the last known per-server catalog for a later re-enable.
      const existingState = this.cache.getInternalServer(id);
      this.cache.registerServer(config, existingState ? existingState.configRevision : 1);
      this.cache.setDisabled(id);
      return { updated: true, disabled: true };
    }

    const existing = this.workers.get(id);
    if (existing) {
      const updated = await existing.updateConfig(config);
      if (options.verifyNow === true) {
        const verification = await existing.subscribeToVerification(options.signal || null);
        return { updated, existing: true, verification };
      }
      return { updated, existing: true };
    }

    const worker = new McpServerWorker(
      config,
      this.cache,
      this.verifyServer,
      this.invokeTool,
      {
        ...options,
        getStaggeredDelay: (baseDelayMs) => this._allocateStaggeredRetryDelay(baseDelayMs),
        onConfigurationChanged: change => this._handleWorkerConfigChange(change)
      }
    );
    worker.on('completed', event => this.emit('verificationCompleted', event));
    worker.on('started', event => this.emit('verificationStarted', event));
    this.workers.set(id, worker);
    worker.start();

    if (options.verifyNow === true) {
      const verification = await worker.subscribeToVerification(options.signal || null);
      return { updated: true, created: true, verification };
    }
    return { updated: true, created: true };
  }

  async removeServer(serverId) {
    const id = String(serverId);
    const worker = this.workers.get(id);
    if (worker) {
      this.workers.delete(id);
      await this._handleWorkerConfigChange({
        previous: cloneConfig(worker.config),
        current: null,
        generation: worker.generation,
        removed: true
      });
      await worker.stop();
    }
    this.cache.removeServer(id);
    return { removed: true, existed: Boolean(worker) };
  }

  async subscribeToVerification(serverId, signal = null) {
    const worker = this.workers.get(String(serverId));
    if (!worker) throw new Error('MCP server is not registered in the poll pool: ' + serverId);
    return worker.subscribeToVerification(signal);
  }

  getWorker(serverId) {
    return this.workers.get(String(serverId)) || null;
  }

  getWorkerCount() {
    return this.workers.size;
  }

  async createTemporaryVerification(config) {
    const id = String(config.id || ('draft-' + crypto.randomUUID()));
    if (this.workers.has(id))
      throw new Error('A worker with this verification id is already registered.');
    const draft = {
      ...cloneConfig(config),
      id,
      name: String(config.name || id),
      enabled: true,
      temporary: true,
      scope: config.scope || (isLoopbackUrl(config.url) ? 'local' : 'remote')
    };
    const worker = new McpServerWorker(draft, this.cache, this.verifyServer, this.invokeTool, { startDelayMs: 0 });
    this.workers.set(id, worker);
    worker.on('completed', event => this.emit('verificationCompleted', event));
    worker.start();

    try {
      // A draft verification is one explicit run. On failure, return its
      // cached result immediately; the editor decides whether to save in a
      // waiting state. Do not silently retry for minutes behind the UI.
      const event = await worker.subscribeToVerification();
      return event;
    } catch (error) {
      await this.removeTemporaryVerification(id);
      throw error;
    }
  }

  getTemporaryVerificationCache(serverId) {
    const id = String(serverId);
    const worker = this.workers.get(id);
    if (!worker || worker.config.temporary !== true)
      throw new Error('Temporary MCP verification worker was not found.');

    const cached = this.cache.getInternalServer(id);
    if (!cached) throw new Error('Temporary MCP verification cache entry was not found.');

    return {
      state: this.cache.getServer(id),
      discoveredServerName: cached.serverInfo && cached.serverInfo.name
        ? String(cached.serverInfo.name)
        : null,
      toolNames: Array.isArray(cached.tools)
        ? cached.tools.map(tool => String(tool.name || '')).filter(Boolean)
        : []
    };
  }

  async removeTemporaryVerification(serverId) {
    const worker = this.workers.get(String(serverId));
    if (!worker || worker.config.temporary !== true)
      return { removed: false };
    return this.removeServer(serverId);
  }

  promoteTemporaryVerification(temporaryServerId, permanentConfig) {
    const promotion = this.configurationTail.then(
      () => this._promoteTemporaryVerification(temporaryServerId, permanentConfig),
      () => this._promoteTemporaryVerification(temporaryServerId, permanentConfig)
    );
    this.configurationTail = promotion.then(() => undefined, () => undefined);
    return promotion;
  }

  async _promoteTemporaryVerification(temporaryServerId, permanentConfig) {
    const temporaryId = String(temporaryServerId);
    const permanentId = String(permanentConfig && permanentConfig.id || '');
    const worker = this.workers.get(temporaryId);
    if (!worker || worker.config.temporary !== true)
      throw new Error('Temporary MCP verification worker was not found.');
    if (!permanentId || permanentId === temporaryId)
      throw new Error('A distinct permanent MCP server id is required.');
    if (worker.running)
      throw new Error('The temporary MCP worker is still verifying.');

    const state = this.cache.getInternalServer(temporaryId);
    if (!state || state.health !== 'online' || !state.lastSuccessAt)
      throw new Error('Only a successfully verified temporary MCP worker can be promoted.');

    const existingPermanent = this.workers.get(permanentId);
    if (existingPermanent && existingPermanent !== worker)
      await this.removeServer(permanentId);
    else if (this.cache.getInternalServer(permanentId))
      this.cache.removeServer(permanentId);

    const previous = cloneConfig(worker.config);
    const next = cloneConfig({ ...permanentConfig, temporary: false });
    await this._handleWorkerConfigChange({
      previous,
      current: next,
      generation: worker.generation + 1,
      promoted: true
    });

    const generation = worker.promoteConfig(next);
    this.workers.delete(temporaryId);
    this.workers.set(permanentId, worker);
    const promotedState = this.cache.promoteServer(temporaryId, next, generation);
    return {
      promoted: true,
      serverId: permanentId,
      configRevision: generation,
      cacheRevision: String(this.cache.cacheRevision),
      completedAt: promotedState.lastCompletedAt
    };
  }

  async stop() {
    this.started = false;
    this._nextRetrySlotAt = 0;
    for (const id of [...this.workers.keys()])
      await this.removeServer(id);
    this.cache.off('changed', this._cacheHandler);
  }

    _allocateStaggeredRetryDelay(baseDelayMs) {
    const now = Date.now();
    const desiredAt = now + Math.max(500, Number(baseDelayMs) || 3000);
    const minSpacingMs = 1100;
    const slotAt = Math.max(desiredAt, this._nextRetrySlotAt + minSpacingMs);
    this._nextRetrySlotAt = slotAt;
    return Math.max(100, slotAt - now);
  }

  async _handleWorkerConfigChange(change) {
    await this.onWorkerConfigurationChanged(change);
  }
}

module.exports = { McpPollManager, McpServerWorker, isLoopbackUrl };
