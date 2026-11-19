'use strict';

const DISABLED_TOOLS_NOTE =
  'Tools or categories listed under disabled_tools (name-only, without description or inputSchema) are currently disabled by the user in MCP Hotplug to save tokens. Do not call them directly; if you need one, ask the user to enable it.';

const ACTION_PARAM_NAMES = ['action', 'command', 'operation', 'subcommand', 'sub_action'];
const TOOLSET_ROOT_SEGMENT = '__toolsets__';
const HOTPLUG_ROOT_SEGMENT = '__hotplug__';

function isPlainObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function cloneJson(value) {
  if (value === undefined) return undefined;
  return JSON.parse(JSON.stringify(value));
}

function encodeSegment(segment) {
  return String(segment || '').replace(/%/g, '%25').replace(/\//g, '%2F');
}

function decodeSegment(segment) {
  return String(segment || '').replace(/%2F/gi, '/').replace(/%25/g, '%');
}

function joinPath(segments) {
  return segments.map(encodeSegment).join('/');
}

function splitPath(nodePath) {
  return String(nodePath || '').split('/').filter(Boolean).map(decodeSegment);
}

function normalizeDisabledList(list) {
  if (!Array.isArray(list)) return [];
  const seen = new Set();
  const result = [];
  for (const raw of list) {
    if (typeof raw !== 'string') continue;
    const trimmed = raw.trim();
    if (!trimmed || seen.has(trimmed)) continue;
    seen.add(trimmed);
    result.push(trimmed);
  }
  return result.sort();
}

function createDisabledSet(disabledList) {
  return new Set(normalizeDisabledList(disabledList));
}

function isPathDisabled(nodePath, disabledSet) {
  if (!disabledSet || disabledSet.size === 0) return false;
  if (disabledSet.has('*') || disabledSet.has(nodePath)) return true;
  const parts = String(nodePath || '').split('/').filter(Boolean);
  let prefix = '';
  for (let i = 0; i < parts.length - 1; i++) {
    prefix = i === 0 ? parts[i] : (prefix + '/' + parts[i]);
    if (disabledSet.has(prefix)) return true;
  }
  return false;
}

function extractSchemaSubActions(tool) {
  const schema = isPlainObject(tool && tool.inputSchema) ? tool.inputSchema : null;
  if (!schema) return null;

  const props = isPlainObject(schema.properties) ? schema.properties : null;
  if (props) {
    for (const paramName of ACTION_PARAM_NAMES) {
      const prop = props[paramName];
      if (!isPlainObject(prop)) continue;
      if (Array.isArray(prop.enum) && prop.enum.length >= 2 && prop.enum.every(v => typeof v === 'string' && v.trim())) {
        return {
          mode: 'enum',
          paramName,
          actions: [...new Set(prop.enum.map(v => String(v)))]
        };
      }
    }
  }

  for (const unionKey of ['oneOf', 'anyOf']) {
    const branches = schema[unionKey];
    if (!Array.isArray(branches) || branches.length < 2) continue;
    for (const paramName of ACTION_PARAM_NAMES) {
      const actions = [];
      let allMatch = true;
      for (const branch of branches) {
        const bProps = isPlainObject(branch && branch.properties) ? branch.properties : null;
        const p = bProps && isPlainObject(bProps[paramName]) ? bProps[paramName] : null;
        if (p && typeof p.const === 'string' && p.const.trim()) {
          actions.push(p.const);
        } else if (p && Array.isArray(p.enum) && p.enum.length === 1 && typeof p.enum[0] === 'string') {
          actions.push(p.enum[0]);
        } else {
          allMatch = false;
          break;
        }
      }
      if (allMatch && actions.length >= 2) {
        return { mode: unionKey, paramName, actions: [...new Set(actions)] };
      }
    }
  }

  return null;
}

function detectMetaPattern(tools) {
  const safeTools = Array.isArray(tools) ? tools : [];
  const names = new Set(safeTools.filter(t => t && typeof t.name === 'string').map(t => t.name));

  if (names.has('list_toolsets') && names.has('describe_toolset')) {
    const callToolName = names.has('call_tool')
      ? 'call_tool'
      : (names.has('execute_tool') ? 'execute_tool' : (names.has('run_tool') ? 'run_tool' : null));
    if (callToolName) {
      return {
        type: 'toolsets',
        listTool: 'list_toolsets',
        describeTool: 'describe_toolset',
        callTool: callToolName,
        metaTools: new Set(['list_toolsets', 'describe_toolset', callToolName])
      };
    }
  }

  if (names.has('all_tools') && names.has('call_tool')) {
    return {
      type: 'hotplug',
      listTool: 'all_tools',
      callTool: 'call_tool',
      metaTools: new Set(['all_tools', 'call_tool'])
    };
  }

  return null;
}

function buildLeafOrSchemaNode(tool, parentSegments, disabledSet, leafMetaType) {
  const name = String(tool.name || '');
  const segments = [...parentSegments, name];
  const nodePath = joinPath(segments);
  const subActions = extractSchemaSubActions(tool);

  if (subActions && subActions.actions.length >= 2) {
    const children = subActions.actions.map(actionName => {
      const actionPath = joinPath([...segments, actionName]);
      return {
        path: actionPath,
        name: actionName,
        displayName: actionName,
        kind: 'tool',
        metaType: 'schema_action',
        description: '',
        enabled: !isPathDisabled(actionPath, disabledSet),
        indeterminate: false,
        children: []
      };
    });
    return finalizeCategoryNode({
      path: nodePath,
      name,
      displayName: name + ' (' + subActions.paramName + ')',
      kind: 'category',
      metaType: 'schema_root',
      description: String(tool.description || ''),
      paramName: subActions.paramName,
      children
    }, disabledSet);
  }

  return {
    path: nodePath,
    name,
    displayName: name,
    kind: 'tool',
    metaType: leafMetaType,
    description: String(tool.description || ''),
    enabled: !isPathDisabled(nodePath, disabledSet),
    indeterminate: false,
    children: []
  };
}

function finalizeCategoryNode(node, disabledSet) {
  const children = Array.isArray(node.children) ? node.children : [];
  if (children.length === 0) {
    const disabled = isPathDisabled(node.path, disabledSet);
    node.enabled = !disabled;
    node.indeterminate = false;
    node.totalLeafCount = 0;
    node.enabledLeafCount = 0;
    return node;
  }

  let totalLeaves = 0;
  let enabledLeaves = 0;
  for (const child of children) {
    if (child.kind === 'tool') {
      totalLeaves += 1;
      if (child.enabled) enabledLeaves += 1;
    } else {
      totalLeaves += Number(child.totalLeafCount || 0);
      enabledLeaves += Number(child.enabledLeafCount || 0);
    }
  }

  node.totalLeafCount = totalLeaves;
  node.enabledLeafCount = enabledLeaves;

  if (totalLeaves === 0) {
    node.enabled = !isPathDisabled(node.path, disabledSet);
    node.indeterminate = false;
  } else if (enabledLeaves === 0) {
    node.enabled = false;
    node.indeterminate = false;
  } else if (enabledLeaves === totalLeaves) {
    node.enabled = true;
    node.indeterminate = false;
  } else {
    node.enabled = false;
    node.indeterminate = true;
  }

  return node;
}

function buildNestedDiscoveredNodes(discovered, parentSegments, disabledSet) {
  if (!isPlainObject(discovered)) return [];

  if (discovered.pattern === 'toolsets' && Array.isArray(discovered.toolsets)) {
    const rootSegments = [...parentSegments, TOOLSET_ROOT_SEGMENT];
    const toolsetNodes = discovered.toolsets.map(ts => {
      const tsName = String(ts.name || '');
      const tsSegments = [...rootSegments, tsName];
      if (ts.isSingleTool === true) {
        return buildLeafOrSchemaNode({
          name: tsName,
          description: String(ts.description || ''),
          inputSchema: isPlainObject(ts.inputSchema) ? ts.inputSchema : {}
        }, rootSegments, disabledSet, 'toolset_tool');
      }
      const tsTools = Array.isArray(ts.tools) ? ts.tools : [];
      const childNodes = tsTools
        .filter(t => t && typeof t.name === 'string' && t.name.trim())
        .map(t => buildLeafOrSchemaNode(t, tsSegments, disabledSet, 'toolset_tool'))
        .sort((a, b) => a.name.localeCompare(b.name));
      if (childNodes.length === 0) return null;

      return finalizeCategoryNode({
        path: joinPath(tsSegments),
        name: tsName,
        displayName: tsName,
        kind: 'category',
        metaType: 'toolset',
        description: String(ts.description || ''),
        children: childNodes
      }, disabledSet);
    }).filter(Boolean).sort((a, b) => a.displayName.localeCompare(b.displayName));

    return [
      finalizeCategoryNode({
        path: joinPath(rootSegments),
        name: 'toolsets',
        displayName: 'Toolsets (list_toolsets / describe_toolset / call_tool)',
        kind: 'category',
        metaType: 'toolset_root',
        description: 'Nested MCP toolsets discovered from this server.',
        children: toolsetNodes
      }, disabledSet)
    ];
  }

  if (discovered.pattern === 'hotplug' && Array.isArray(discovered.servers)) {
    const rootSegments = [...parentSegments, HOTPLUG_ROOT_SEGMENT];
    const serverNodes = discovered.servers.map(srv => {
      const srvId = String(srv.id || srv.name || 'server');
      const srvName = String(srv.name || srvId);
      const srvSegments = [...rootSegments, srvId];
      let children = [];
      if (srv.nested && isPlainObject(srv.nested)) {
        children = buildNestedDiscoveredNodes(srv.nested, srvSegments, disabledSet);
      } else {
        const srvTools = Array.isArray(srv.tools) ? srv.tools : [];
        children = srvTools
          .filter(t => t && typeof t.name === 'string' && t.name.trim())
          .map(t => buildLeafOrSchemaNode(t, srvSegments, disabledSet, 'hotplug_tool'))
          .sort((a, b) => a.name.localeCompare(b.name));
      }

      return finalizeCategoryNode({
        path: joinPath(srvSegments),
        name: srvId,
        displayName: srvName,
        kind: 'category',
        metaType: 'hotplug_server',
        description: '',
        children
      }, disabledSet);
    }).sort((a, b) => a.displayName.localeCompare(b.displayName));

    return [
      finalizeCategoryNode({
        path: joinPath(rootSegments),
        name: 'hotplug',
        displayName: 'Hotplug Catalog (all_tools / call_tool)',
        kind: 'category',
        metaType: 'hotplug_root',
        description: 'Nested MCP catalog discovered via all_tools.',
        children: serverNodes
      }, disabledSet)
    ];
  }

  return [];
}

function buildServerToolTree(tools, discoveredNested, disabledList) {
  const safeTools = Array.isArray(tools) ? tools : [];
  const disabledSet = createDisabledSet(disabledList);
  const metaPattern = detectMetaPattern(safeTools);
  const nodes = [];

  const hasDiscoveredMeta = Boolean(
    metaPattern &&
    isPlainObject(discoveredNested) &&
    ((metaPattern.type === 'toolsets' && discoveredNested.pattern === 'toolsets' && Array.isArray(discoveredNested.toolsets) && discoveredNested.toolsets.length > 0) ||
     (metaPattern.type === 'hotplug' && discoveredNested.pattern === 'hotplug' && Array.isArray(discoveredNested.servers) && discoveredNested.servers.length > 0))
  );

  if (hasDiscoveredMeta) {
    nodes.push(...buildNestedDiscoveredNodes(discoveredNested, [], disabledSet));
    for (const tool of safeTools) {
      if (!tool || typeof tool.name !== 'string' || !tool.name.trim()) continue;
      if (metaPattern.metaTools.has(tool.name)) continue;
      nodes.push(buildLeafOrSchemaNode(tool, [], disabledSet, 'flat'));
    }
  } else {
    for (const tool of safeTools) {
      if (!tool || typeof tool.name !== 'string' || !tool.name.trim()) continue;
      nodes.push(buildLeafOrSchemaNode(tool, [], disabledSet, 'flat'));
    }
  }

  nodes.sort((a, b) => {
    if (a.kind !== b.kind) return a.kind === 'category' ? -1 : 1;
    return a.displayName.localeCompare(b.displayName);
  });

  let totalLeafCount = 0;
  let enabledLeafCount = 0;
  for (const node of nodes) {
    if (node.kind === 'tool') {
      totalLeafCount += 1;
      if (node.enabled) enabledLeafCount += 1;
    } else {
      totalLeafCount += Number(node.totalLeafCount || 0);
      enabledLeafCount += Number(node.enabledLeafCount || 0);
    }
  }

  const allDisabled = (totalLeafCount > 0 && enabledLeafCount === 0) ||
    (disabledSet.has('*') && (nodes.length > 0 || safeTools.length > 0));

  return {
    nodes,
    totalLeafCount,
    enabledLeafCount,
    allDisabled,
    hasMetaPattern: Boolean(metaPattern),
    metaPatternType: metaPattern ? metaPattern.type : null,
    hasDiscoveredMeta,
    discoveredAt: isPlainObject(discoveredNested) ? (discoveredNested.discoveredAt || null) : null
  };
}

// Variant B: Even when a parent category is disabled, emit the full nested
// name-only hierarchy underneath so the AI knows indirectly what capabilities exist.
function buildCompactDisabledHierarchy(tree) {
  function collect(node) {
    if (node.kind === 'tool') {
      return node.enabled ? null : node.name;
    }

    const childItems = [];
    for (const child of node.children || []) {
      const item = collect(child);
      if (item !== null) childItems.push(item);
    }

    if (childItems.length === 0) {
      return (!node.enabled && !node.indeterminate && (node.children || []).length === 0)
        ? node.name
        : null;
    }

    if (node.metaType === 'toolset_root' || node.metaType === 'hotplug_root') {
      return { __unwrap__: true, items: childItems };
    }

    return { [node.name]: childItems };
  }

  const output = [];
  for (const node of (tree && tree.nodes) || []) {
    const item = collect(node);
    if (item === null) continue;
    if (isPlainObject(item) && item.__unwrap__ === true) {
      output.push(...item.items);
    } else {
      output.push(item);
    }
  }
  return output;
}

function pruneSchemaForDisabledActions(tool, toolPath, disabledSet) {
  const subActions = extractSchemaSubActions(tool);
  if (!subActions) return tool;

  const allowedActions = subActions.actions.filter(action =>
    !isPathDisabled(joinPath([...splitPath(toolPath), action]), disabledSet)
  );
  if (allowedActions.length === subActions.actions.length) return tool;
  if (allowedActions.length === 0) return null;

  const copy = cloneJson(tool);
  if (subActions.mode === 'enum' && copy.inputSchema && copy.inputSchema.properties && copy.inputSchema.properties[subActions.paramName]) {
    copy.inputSchema.properties[subActions.paramName].enum = allowedActions;
  } else if ((subActions.mode === 'oneOf' || subActions.mode === 'anyOf') && copy.inputSchema && Array.isArray(copy.inputSchema[subActions.mode])) {
    const allowedSet = new Set(allowedActions);
    copy.inputSchema[subActions.mode] = copy.inputSchema[subActions.mode].filter(branch => {
      const p = branch && branch.properties && branch.properties[subActions.paramName];
      const val = p && (typeof p.const === 'string' ? p.const : (Array.isArray(p.enum) ? p.enum[0] : null));
      return !val || allowedSet.has(val);
    });
  }
  return copy;
}

function filterCatalogToolsForServer(tools, discoveredNested, disabledList) {
  const safeTools = Array.isArray(tools) ? tools : [];
  const disabledSet = createDisabledSet(disabledList);
  const tree = buildServerToolTree(safeTools, discoveredNested, disabledList);
  const disabledHierarchy = buildCompactDisabledHierarchy(tree);

  if (tree.allDisabled) {
    return {
      tools: [],
      disabledTools: disabledHierarchy,
      allDisabled: true,
      tree
    };
  }

  const metaPattern = detectMetaPattern(safeTools);
  const enabledTools = [];

  for (const rawTool of safeTools) {
    if (!rawTool || typeof rawTool.name !== 'string' || !rawTool.name.trim()) continue;
    const toolName = rawTool.name;

    if (metaPattern && metaPattern.metaTools.has(toolName) && tree.hasDiscoveredMeta) {
      const rootSeg = metaPattern.type === 'toolsets' ? TOOLSET_ROOT_SEGMENT : HOTPLUG_ROOT_SEGMENT;
      const rootNode = tree.nodes.find(n => n.path === rootSeg);
      if (rootNode && !rootNode.enabled && !rootNode.indeterminate) {
        continue;
      }
      if (isPathDisabled(encodeSegment(toolName), disabledSet)) {
        continue;
      }
      enabledTools.push(rawTool);
      continue;
    }

    const toolPath = encodeSegment(toolName);
    if (isPathDisabled(toolPath, disabledSet)) continue;

    const pruned = pruneSchemaForDisabledActions(rawTool, toolPath, disabledSet);
    if (pruned) enabledTools.push(pruned);
  }

  return {
    tools: enabledTools,
    disabledTools: disabledHierarchy,
    allDisabled: enabledTools.length === 0 && safeTools.length > 0,
    tree
  };
}

function checkToolCallAllowed(toolName, args, tools, discoveredNested, disabledList) {
  const safeTools = Array.isArray(tools) ? tools : [];
  const disabledSet = createDisabledSet(disabledList);
  const safeArgs = isPlainObject(args) ? args : {};
  const tree = buildServerToolTree(safeTools, discoveredNested, disabledList);

  const blocked = (targetLabel) => ({
    allowed: false,
    code: 'TOOL_DISABLED',
    message:
      'Tool "' + targetLabel + '" is currently disabled by the user in MCP Hotplug to save tokens. ' +
      'Ask the user to enable it in MCP Hotplug if this operation is needed.'
  });

  if (tree.allDisabled || disabledSet.has('*')) {
    return blocked(toolName);
  }

  const topPath = encodeSegment(toolName);
  if (isPathDisabled(topPath, disabledSet)) {
    return blocked(toolName);
  }

  const metaPattern = detectMetaPattern(safeTools);

  if (metaPattern && metaPattern.type === 'toolsets') {
    if (isPathDisabled(TOOLSET_ROOT_SEGMENT, disabledSet)) {
      if (metaPattern.metaTools.has(toolName)) return blocked(toolName);
    }

    const rootNode = tree.nodes.find(n => n.path === TOOLSET_ROOT_SEGMENT);
    if (rootNode && !rootNode.enabled && !rootNode.indeterminate && metaPattern.metaTools.has(toolName)) {
      return blocked(toolName);
    }

    if (toolName === metaPattern.callTool) {
      const requestedToolset = typeof safeArgs.toolset_name === 'string' ? safeArgs.toolset_name.trim() : '';
      const requestedTool = typeof safeArgs.tool_name === 'string'
        ? safeArgs.tool_name.trim()
        : (typeof safeArgs.name === 'string' ? safeArgs.name.trim() : '');
      const nestedArgs = isPlainObject(safeArgs.arguments) ? safeArgs.arguments : {};

      if (requestedToolset) {
        const tsPath = joinPath([TOOLSET_ROOT_SEGMENT, requestedToolset]);
        if (isPathDisabled(tsPath, disabledSet)) {
          return blocked(requestedToolset + '/' + (requestedTool || '*'));
        }
        if (requestedTool) {
          const leafPath = joinPath([TOOLSET_ROOT_SEGMENT, requestedToolset, requestedTool]);
          if (isPathDisabled(leafPath, disabledSet)) {
            return blocked(requestedToolset + '/' + requestedTool);
          }
          for (const actionParam of ACTION_PARAM_NAMES) {
            if (typeof nestedArgs[actionParam] === 'string' && nestedArgs[actionParam].trim()) {
              const actionPath = joinPath([TOOLSET_ROOT_SEGMENT, requestedToolset, requestedTool, nestedArgs[actionParam].trim()]);
              if (isPathDisabled(actionPath, disabledSet)) {
                return blocked(requestedToolset + '/' + requestedTool + '/' + nestedArgs[actionParam].trim());
              }
            }
          }
        }
      } else if (requestedTool && isPlainObject(discoveredNested) && Array.isArray(discoveredNested.toolsets)) {
        // toolset_name is optional in Unreal call_tool; resolve against discovered toolsets.
        const matches = [];
        for (const ts of discoveredNested.toolsets) {
          const tsTools = Array.isArray(ts.tools) ? ts.tools : [];
          if (tsTools.some(t => t && t.name === requestedTool)) {
            matches.push(ts.name);
          }
        }
        if (matches.length > 0) {
          const allMatchedDisabled = matches.every(tsName => {
            const leafPath = joinPath([TOOLSET_ROOT_SEGMENT, tsName, requestedTool]);
            if (isPathDisabled(leafPath, disabledSet)) return true;
            for (const actionParam of ACTION_PARAM_NAMES) {
              if (typeof nestedArgs[actionParam] === 'string' && nestedArgs[actionParam].trim()) {
                const actionPath = joinPath([TOOLSET_ROOT_SEGMENT, tsName, requestedTool, nestedArgs[actionParam].trim()]);
                if (isPathDisabled(actionPath, disabledSet)) return true;
              }
            }
            return false;
          });
          if (allMatchedDisabled) {
            return blocked(matches[0] + '/' + requestedTool);
          }
        }
      }
    }
  }

  if (metaPattern && metaPattern.type === 'hotplug') {
    if (isPathDisabled(HOTPLUG_ROOT_SEGMENT, disabledSet) && metaPattern.metaTools.has(toolName)) {
      return blocked(toolName);
    }
    if (toolName === 'call_tool' && typeof safeArgs.tool_id === 'string') {
      const sep = safeArgs.tool_id.indexOf('::');
      if (sep > 0) {
        let srvId = safeArgs.tool_id.slice(0, sep);
        let subTool = safeArgs.tool_id.slice(sep + 2);
        try { srvId = decodeURIComponent(srvId); } catch (_) {}
        try { subTool = decodeURIComponent(subTool); } catch (_) {}
        const targetPath = joinPath([HOTPLUG_ROOT_SEGMENT, srvId, subTool]);
        if (isPathDisabled(targetPath, disabledSet)) {
          return blocked(srvId + '/' + subTool);
        }
      }
    }
  }

  const matchedTool = safeTools.find(t => t && t.name === toolName);
  if (matchedTool) {
    const subActions = extractSchemaSubActions(matchedTool);
    if (subActions) {
      const val = safeArgs[subActions.paramName];
      if (typeof val === 'string' && val.trim()) {
        const actionPath = joinPath([toolName, val.trim()]);
        if (isPathDisabled(actionPath, disabledSet)) {
          return blocked(toolName + '/' + val.trim());
        }
      }
    }
  }

  return { allowed: true };
}

function extractJsonFromToolResult(result) {
  if (!isPlainObject(result)) return null;
  if (result.structuredContent !== undefined && result.structuredContent !== null) {
    return { data: cloneJson(result.structuredContent), source: 'structuredContent' };
  }
  const content = Array.isArray(result.content) ? result.content : [];
  for (let i = 0; i < content.length; i++) {
    const block = content[i];
    if (!block || block.type !== 'text' || typeof block.text !== 'string') continue;
    let text = block.text.trim();
    if (!text) continue;
    const fence = text.match(/^```(?:json)?\s*([\s\S]*?)\s*```$/i);
    if (fence) text = fence[1].trim();
    try {
      return { data: JSON.parse(text), source: 'text', blockIndex: i };
    } catch (_) {}
  }
  return null;
}

function writeFilteredJsonToToolResult(originalResult, extracted, filteredData) {
  const copy = cloneJson(originalResult);
  const serialized = JSON.stringify(filteredData);
  if (copy.structuredContent !== undefined) {
    copy.structuredContent = filteredData;
  }
  if (Array.isArray(copy.content)) {
    const idx = typeof extracted.blockIndex === 'number'
      ? extracted.blockIndex
      : copy.content.findIndex(b => b && b.type === 'text');
    if (idx >= 0) {
      copy.content[idx] = { ...copy.content[idx], type: 'text', text: serialized };
    } else {
      copy.content.push({ type: 'text', text: serialized });
    }
  } else {
    copy.content = [{ type: 'text', text: serialized }];
  }
  return copy;
}

function parseToolsetsListFromData(data, rawTextFallback = '') {
  const result = [];
  const seen = new Set();

  function add(name, description = '') {
    const clean = String(name || '').trim();
    if (!clean || seen.has(clean)) return;
    seen.add(clean);
    result.push({ name: clean, description: String(description || '') });
  }

  function consumeArray(arr) {
    for (const item of arr) {
      if (typeof item === 'string') {
        add(item, '');
      } else if (isPlainObject(item)) {
        const name = item.name || item.toolset_name || item.toolset || item.id || item.title;
        if (name) add(name, item.description || item.summary || '');
      }
    }
  }

  if (Array.isArray(data)) {
    consumeArray(data);
  } else if (isPlainObject(data)) {
    const listProp = data.toolsets || data.categories || data.groups || data.items || data.result;
    if (Array.isArray(listProp)) {
      consumeArray(listProp);
    } else if (isPlainObject(listProp)) {
      for (const [key, val] of Object.entries(listProp)) {
        add(key, isPlainObject(val) ? (val.description || '') : (typeof val === 'string' ? val : ''));
      }
    } else {
      for (const [key, val] of Object.entries(data)) {
        if (['status', 'count', 'total', 'disabled_tools_note', 'disabled_tools', 'disabled_toolsets'].includes(key)) continue;
        if (isPlainObject(val)) add(key, val.description || '');
        else if (typeof val === 'string') add(key, val);
      }
    }
  }

  if (result.length === 0 && rawTextFallback) {
    for (const line of String(rawTextFallback).split(/\r?\n/)) {
      const m = line.match(/^\s{0,1}(?:[-*+]|\d+\.)\s+`?([A-Za-z0-9_.:-]+)`?(?:\s*[:\-–—]\s*(.*))?$/);
      if (m) add(m[1], m[2] || '');
    }
  }

  return result;
}

function parseToolsetToolsFromData(data, rawTextFallback = '') {
  const result = [];
  const seen = new Set();

  function addTool(raw, fallbackName = '') {
    if (typeof raw === 'string') {
      const clean = raw.trim();
      if (!clean || seen.has(clean)) return;
      seen.add(clean);
      result.push({ name: clean, description: '', inputSchema: {} });
      return;
    }
    if (!isPlainObject(raw)) return;
    const name = String(raw.name || raw.tool_name || raw.id || fallbackName || '').trim();
    if (!name || seen.has(name)) return;
    seen.add(name);
    result.push({
      name,
      description: String(raw.description || ''),
      inputSchema: isPlainObject(raw.inputSchema)
        ? raw.inputSchema
        : (isPlainObject(raw.input_schema) ? raw.input_schema : (isPlainObject(raw.parameters) ? raw.parameters : {}))
    });
  }

  let source = data;
  if (isPlainObject(source) && isPlainObject(source.result)) source = source.result;
  if (isPlainObject(source) && isPlainObject(source.data)) source = source.data;
  if (isPlainObject(source) && isPlainObject(source.toolset)) source = source.toolset;

  if (Array.isArray(source)) {
    for (const item of source) addTool(item);
  } else if (isPlainObject(source)) {
    const arr = source.tools || source.items || source.methods || source.actions || source.functions || source.definitions;
    if (Array.isArray(arr)) {
      for (const item of arr) addTool(item);
    } else if (isPlainObject(arr)) {
      for (const [k, v] of Object.entries(arr)) addTool(v, k);
    } else if (source.name && (isPlainObject(source.inputSchema) || isPlainObject(source.input_schema) || isPlainObject(source.parameters))) {
      addTool(source);
    }
  }

  if (result.length === 0 && rawTextFallback) {
    for (const line of String(rawTextFallback).split(/\r?\n/)) {
      const m = line.match(/^\s*(?:[-*+]|\d+\.|##+)\s+`?([A-Za-z0-9_.:-]+)`?(?:\s*[:\-–—]\s*(.*))?$/);
      if (m && !['Tools', 'Toolset', 'Parameters', 'Description', 'Schema'].includes(m[1])) {
        addTool({ name: m[1], description: m[2] || '' });
      }
    }
  }

  return result;
}

function filterToolCallResponse(toolName, args, result, tools, discoveredNested, disabledList) {
  if (!isPlainObject(result) || result.isError === true) return result;
  const safeTools = Array.isArray(tools) ? tools : [];
  const metaPattern = detectMetaPattern(safeTools);
  if (!metaPattern) return result;

  const disabledSet = createDisabledSet(disabledList);
  const safeArgs = isPlainObject(args) ? args : {};
  const extracted = extractJsonFromToolResult(result);
  if (!extracted) return result;

  if (metaPattern.type === 'toolsets' && toolName === metaPattern.listTool) {
    const data = extracted.data;
    const disabledToolsets = [];
    const filterEntry = (entry) => {
      const tsName = typeof entry === 'string'
        ? entry
        : (isPlainObject(entry) ? String(entry.name || entry.toolset_name || entry.id || '') : '');
      if (!tsName) return true;
      const tsPath = joinPath([TOOLSET_ROOT_SEGMENT, tsName]);
      const tsObj = isPlainObject(discoveredNested) && Array.isArray(discoveredNested.toolsets)
        ? discoveredNested.toolsets.find(t => t && t.name === tsName)
        : null;
      const tsTools = tsObj && Array.isArray(tsObj.tools) ? tsObj.tools : [];
      const allChildrenDisabled = tsTools.length > 0 && tsTools.every(t =>
        isPathDisabled(joinPath([TOOLSET_ROOT_SEGMENT, tsName, t.name]), disabledSet)
      );

      if (isPathDisabled(tsPath, disabledSet) || allChildrenDisabled) {
        if (tsTools.length > 0) {
          disabledToolsets.push({ [tsName]: tsTools.map(t => t.name) });
        } else {
          disabledToolsets.push(tsName);
        }
        return false;
      }
      return true;
    };

    let filteredPayload;
    if (Array.isArray(data)) {
      const enabled = data.filter(filterEntry);
      filteredPayload = {
        toolsets: enabled,
        ...(disabledToolsets.length > 0 ? { disabled_tools: disabledToolsets } : {}),
        disabled_tools_note: DISABLED_TOOLS_NOTE
      };
    } else if (isPlainObject(data)) {
      filteredPayload = cloneJson(data);
      for (const key of ['toolsets', 'categories', 'groups', 'items']) {
        if (Array.isArray(filteredPayload[key])) {
          filteredPayload[key] = filteredPayload[key].filter(filterEntry);
          break;
        }
      }
      if (disabledToolsets.length > 0) {
        filteredPayload.disabled_tools = disabledToolsets;
      }
      filteredPayload.disabled_tools_note = DISABLED_TOOLS_NOTE;
    } else {
      return result;
    }

    return writeFilteredJsonToToolResult(result, extracted, filteredPayload);
  }

  if (metaPattern.type === 'toolsets' && toolName === metaPattern.describeTool) {
    const tsName = typeof safeArgs.toolset_name === 'string' ? safeArgs.toolset_name.trim() : '';
    if (!tsName) return result;

    const data = extracted.data;
    const disabledNames = [];
    const filterToolItem = (item) => {
      const name = typeof item === 'string'
        ? item
        : (isPlainObject(item) ? String(item.name || item.tool_name || item.id || '') : '');
      if (!name) return item;
      const leafPath = joinPath([TOOLSET_ROOT_SEGMENT, tsName, name]);
      if (isPathDisabled(leafPath, disabledSet)) {
        disabledNames.push(name);
        return null;
      }
      if (isPlainObject(item)) {
        const pruned = pruneSchemaForDisabledActions(item, leafPath, disabledSet);
        if (!pruned) {
          disabledNames.push(name);
          return null;
        }
        return pruned;
      }
      return item;
    };

    let filteredPayload;
    if (Array.isArray(data)) {
      const enabled = data.map(filterToolItem).filter(Boolean);
      filteredPayload = {
        toolset_name: tsName,
        tools: enabled,
        ...(disabledNames.length > 0 ? { disabled_tools: disabledNames } : {}),
        disabled_tools_note: DISABLED_TOOLS_NOTE
      };
    } else if (isPlainObject(data)) {
      filteredPayload = cloneJson(data);
      const container = isPlainObject(filteredPayload.toolset) ? filteredPayload.toolset : filteredPayload;
      for (const key of ['tools', 'items', 'methods', 'actions', 'functions']) {
        if (Array.isArray(container[key])) {
          container[key] = container[key].map(filterToolItem).filter(Boolean);
          break;
        }
      }
      if (disabledNames.length > 0) {
        filteredPayload.disabled_tools = disabledNames;
      }
      filteredPayload.disabled_tools_note = DISABLED_TOOLS_NOTE;
    } else {
      return result;
    }

    return writeFilteredJsonToToolResult(result, extracted, filteredPayload);
  }

  if (metaPattern.type === 'hotplug' && toolName === 'all_tools') {
    const data = extracted.data;
    if (!isPlainObject(data) || !Array.isArray(data.tools)) return result;

    const filteredPayload = cloneJson(data);
    const disabledByServer = new Map();
    filteredPayload.tools = filteredPayload.tools.filter(item => {
      if (!item || typeof item.name !== 'string') return false;
      const srvId = String((item.source && item.source.server_id) || 'unknown');
      const leafPath = joinPath([HOTPLUG_ROOT_SEGMENT, srvId, item.name]);
      if (isPathDisabled(leafPath, disabledSet)) {
        if (!disabledByServer.has(srvId)) disabledByServer.set(srvId, []);
        disabledByServer.get(srvId).push(item.name);
        return false;
      }
      return true;
    });

    if (disabledByServer.size > 0) {
      filteredPayload.disabled_tools = [...disabledByServer.entries()].map(([srvId, names]) => ({ [srvId]: names }));
    }
    filteredPayload.disabled_tools_note = DISABLED_TOOLS_NOTE;
    return writeFilteredJsonToToolResult(result, extracted, filteredPayload);
  }

  return result;
}

async function discoverNestedForServer(callToolFn, tools, depth = 0, maxDepth = 5) {
  if (depth >= maxDepth) return null;
  const metaPattern = detectMetaPattern(tools);
  if (!metaPattern) return null;

  if (metaPattern.type === 'toolsets') {
    const listRes = await callToolFn(metaPattern.listTool, {});
    const extractedList = extractJsonFromToolResult(listRes);
    const rawText = Array.isArray(listRes && listRes.content)
      ? listRes.content.filter(b => b && b.type === 'text').map(b => String(b.text || '')).join('\n')
      : '';
    const toolsets = parseToolsetsListFromData(extractedList ? extractedList.data : null, rawText);
    if (toolsets.length === 0) return null;

    const detailed = [];
    const batchSize = 4;
    for (let i = 0; i < toolsets.length; i += batchSize) {
      const slice = toolsets.slice(i, i + batchSize);
      const batchResults = await Promise.all(slice.map(async ts => {
        for (let attempt = 0; attempt < 2; attempt++) {
          try {
            const descRes = await callToolFn(metaPattern.describeTool, { toolset_name: ts.name });
            if (descRes && descRes.isError === true) return null;
            const extractedDesc = extractJsonFromToolResult(descRes);
            const rawDescText = Array.isArray(descRes && descRes.content)
              ? descRes.content.filter(b => b && b.type === 'text').map(b => String(b.text || '')).join('\n')
              : '';
            if (/not found|unknown toolset/i.test(rawDescText)) return null;
            const tsTools = parseToolsetToolsFromData(extractedDesc ? extractedDesc.data : null, rawDescText);
            if (tsTools.length === 1 && tsTools[0].name === ts.name) {
              return { name: ts.name, description: tsTools[0].description || ts.description, isSingleTool: true, inputSchema: tsTools[0].inputSchema, tools: [] };
            }
            if (tsTools.length === 0) return null;
            return { name: ts.name, description: ts.description, tools: tsTools };
          } catch (_) {
            if (attempt === 1) return null;
          }
        }
        return null;
      }));
      detailed.push(...batchResults.filter(Boolean));
    }

    return {
      pattern: 'toolsets',
      schemaVersion: 2,
      discoveredAt: new Date().toISOString(),
      toolsets: detailed
    };
  }

  if (metaPattern.type === 'hotplug') {
    const catalogRes = await callToolFn('all_tools', {});
    const extracted = extractJsonFromToolResult(catalogRes);
    const data = extracted && extracted.data;
    if (!isPlainObject(data)) return null;

    const serversMap = new Map();
    for (const srv of Array.isArray(data.servers) ? data.servers : []) {
      const id = String(srv.id || srv.serverId || '');
      if (!id) continue;
      serversMap.set(id, { id, name: String(srv.name || id), tools: [], nested: null });
    }
    for (const tool of Array.isArray(data.tools) ? data.tools : []) {
      const srvId = String((tool.source && tool.source.server_id) || 'unknown');
      const srvName = String((tool.source && tool.source.server_name) || srvId);
      if (!serversMap.has(srvId)) {
        serversMap.set(srvId, { id: srvId, name: srvName, tools: [], nested: null });
      }
      serversMap.get(srvId).tools.push({
        name: String(tool.name || ''),
        description: String(tool.description || ''),
        inputSchema: isPlainObject(tool.inputSchema) ? tool.inputSchema : {}
      });
    }

    const servers = [...serversMap.values()];
    const catalogRevision = String(data.catalog_revision || '');
    for (const srv of servers) {
      const subPattern = detectMetaPattern(srv.tools);
      if (subPattern && depth + 1 < maxDepth) {
        try {
          srv.nested = await discoverNestedForServer(
            (subToolName, subArgs) => callToolFn('call_tool', {
              tool_id: encodeURIComponent(srv.id) + '::' + encodeURIComponent(subToolName),
              catalog_revision: catalogRevision,
              arguments: subArgs || {}
            }),
            srv.tools,
            depth + 1,
            maxDepth
          );
        } catch (_) {}
      }
    }

    return {
      pattern: 'hotplug',
      discoveredAt: new Date().toISOString(),
      servers
    };
  }

  return null;
}

module.exports = {
  DISABLED_TOOLS_NOTE,
  TOOLSET_ROOT_SEGMENT,
  HOTPLUG_ROOT_SEGMENT,
  encodeSegment,
  decodeSegment,
  joinPath,
  splitPath,
  normalizeDisabledList,
  createDisabledSet,
  isPathDisabled,
  detectMetaPattern,
  extractSchemaSubActions,
  buildServerToolTree,
  buildCompactDisabledHierarchy,
  filterCatalogToolsForServer,
  checkToolCallAllowed,
  filterToolCallResponse,
  discoverNestedForServer
};