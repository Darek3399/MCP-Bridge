'use strict';

const { validate: validateSchema } = require('./hotplug-schema-validator');

function toolError(code, message, details = {}) {
  const payload = { code, message, ...details };
  return {
    isError: true,
    content: [{ type: 'text', text: JSON.stringify(payload) }],
    structuredContent: payload
  };
}

class HotplugToolDispatcher {
  constructor(routerClient, catalogManager) {
    this.routerClient = routerClient;
    this.catalogManager = catalogManager;
  }

  async call({ toolId, catalogRevision, arguments: toolArguments }) {
    let descriptor = this.catalogManager.getCachedDescriptor(toolId);
    if (!descriptor) {
      try { await this.catalogManager.getCatalog(); } catch (_) {}
      descriptor = this.catalogManager.getCachedDescriptor(toolId);
    }
    if (!descriptor) {
      const disabled = this.catalogManager.getDisabledDescriptor(toolId);
      if (disabled) {
        return toolError(
          'TOOL_DISABLED',
          'Tool "' + disabled.originalName + '" on server "' + disabled.serverName + '" is currently disabled by the user in MCP Hotplug to save tokens. Ask the user to enable it if needed.',
          { requested_tool_id: toolId, server_id: disabled.serverId }
        );
      }
      return toolError(
        'TOOL_NOT_FOUND',
        'This tool is not present in the last cached catalog returned by all_tools. Call all_tools to read the current cache snapshot and select a listed tool.',
        { requested_tool_id: toolId, catalog_revision: String(catalogRevision || '') }
      );
    }

    let currentDescriptor;
    try {
      currentDescriptor = await this.catalogManager.getCurrentServerToolDescriptor(descriptor);
    } catch (error) {
      return toolError(
        'SERVER_CACHE_UNAVAILABLE',
        'Hotplug could not read the selected server cache entry. No upstream MCP request was made.',
        {
          requested_tool_id: toolId,
          server_id: descriptor.serverId,
          error: error instanceof Error ? error.message : String(error)
        }
      );
    }

    if (!currentDescriptor) {
      return toolError(
        'TOOL_NOT_FOUND',
        'The selected tool is no longer present in the selected server cache. Call all_tools to read the latest cached catalog and select a listed tool.',
        { requested_tool_id: toolId, server_id: descriptor.serverId }
      );
    }

    if (currentDescriptor.disabled === true) {
      return toolError(
        'TOOL_DISABLED',
        'Tool "' + descriptor.originalName + '" on server "' + descriptor.serverName + '" is currently disabled by the user in MCP Hotplug to save tokens. Ask the user to enable it if needed.',
        { requested_tool_id: toolId, server_id: descriptor.serverId }
      );
    }

    const args = toolArguments && typeof toolArguments === 'object' && !Array.isArray(toolArguments)
      ? toolArguments : {};

    const validationErrors = validateSchema(currentDescriptor.inputSchema, args);
    if (validationErrors.length > 0) {
      return toolError(
        'INVALID_ARGUMENTS',
        'Arguments do not match the selected tool inputSchema. Correct the listed paths and retry call_tool.',
        {
          requested_tool_id: toolId,
          server_id: descriptor.serverId,
          validation_errors: validationErrors.slice(0, 50)
        }
      );
    }

    try {
      const response = await this.routerClient.callServerTool(
        descriptor.serverId,
        descriptor.originalName,
        currentDescriptor.serverRevision,
        args
      );
      const result = response && response.result;
      if (!result || typeof result !== 'object') {
        throw new Error('The upstream MCP returned an invalid tools/call result.');
      }

      if (result.isError === true) {
        const errorText = (Array.isArray(result.content) ? result.content : [])
          .filter(item => item && item.type === 'text')
          .map(item => String(item.text || ''))
          .join('\n') + ' ' + JSON.stringify(result.structuredContent || {});

        if (/unknown tool|no such tool|tool\s+(?:not found|not available|disabled)|tool.{0,40}(?:not found|not available|disabled)|method not found|unknown method|инструмент.{0,40}(?:не найден|недоступ|отключ)|(?:не найден|недоступ|отключ).{0,40}инструмент/i.test(errorText)) {
          return toolError(
            'TOOL_NOT_AVAILABLE',
            'The selected upstream tool is no longer available in the cache. Call all_tools to read the latest cached catalog; the Poll Manager updates it on the worker’s independent schedule.',
            { requested_tool_id: toolId, server_id: descriptor.serverId, upstream_message: errorText }
          );
        }

        if (/ошибка вызова|сервер не ответил|econnrefused|econnreset|ehostunreach|enetunreach|ehostdown|eai_again|connection refused|connection reset|failed to fetch|fetch failed|network error|timed out|timeout|aborterror|operation was aborted|socket hang up|socket.*closed|no route to host|broken pipe|upstream.*unavailable/i.test(errorText)) {
          return toolError(
            'UPSTREAM_UNAVAILABLE',
            'The upstream MCP server did not complete this call. The Poll Manager continues its independent health checks; all_tools only reads the latest cached state. Do not automatically repeat a potentially side-effecting operation.',
            { requested_tool_id: toolId, server_id: descriptor.serverId, upstream_message: errorText }
          );
        }
      }
      return result;
    } catch (error) {
      const code = error && typeof error.code === 'string' ? error.code : '';
      if (code === 'TOOL_DISABLED') {
        return toolError('TOOL_DISABLED', error instanceof Error ? error.message : 'The selected tool is disabled by the user in MCP Hotplug to save tokens.', { requested_tool_id: toolId, server_id: descriptor.serverId });
      }
      if (code === 'TOOL_VERSION_CHANGED') {
        return toolError('CATALOG_CHANGED', 'The selected server changed immediately before dispatch, so no tool call was made. Call all_tools to read the latest cached catalog, then select the tool again.', { requested_tool_id: toolId, server_id: descriptor.serverId });
      }
      if (code === 'TOOL_NOT_AVAILABLE') {
        return toolError('TOOL_NOT_AVAILABLE', 'The selected tool is no longer present in the selected server cache. Call all_tools to read the latest cached catalog.', { requested_tool_id: toolId, server_id: descriptor.serverId });
      }
      if (code === 'INVALID_CACHED_SCHEMA') {
        return toolError('INVALID_CACHED_SCHEMA', 'The cached MCP schema contains invalid parameter-header annotations. No upstream tool call was made.', { requested_tool_id: toolId, server_id: descriptor.serverId });
      }
      return toolError(
        'UPSTREAM_UNAVAILABLE',
        'The upstream MCP could not complete this call. The Poll Manager continues its independent health checks; all_tools only reads the latest cached state. Do not automatically repeat a potentially side-effecting operation.',
        { requested_tool_id: toolId, server_id: descriptor.serverId, error: error instanceof Error ? error.message : String(error) }
      );
    }
  }
}

module.exports = { HotplugToolDispatcher };