'use strict';

const HOTPLUG_CONTROL_SERVER_ID = 'hotplug_control';
const HOTPLUG_CONTROL_MANAGE_TOOL = 'manage_tools';
const HOTPLUG_CONTROL_LIST_TOOL = 'list_tools';

const DISABLED_TOOLS_NOTE_USER_ONLY =
  'Tools or categories listed under disabled_tools (name-only, without description or inputSchema) are currently disabled by the user in MCP Hotplug to save tokens. Do not call them directly; if you need one, ask the user to enable it.';

const DISABLED_TOOLS_NOTE_SELF_MANAGE =
  'disabled_tools lists hidden capabilities (name-only) disabled in MCP Hotplug to save tokens. Do not call them directly. ' +
  'To trim unused servers/categories/tools for a task or enable a missing disabled tool, batch all changes in ONE call via ' +
  'hotplug_control::manage_tools (call hotplug_control::list_tools first only if you need exact full names). ' +
  'Avoid frequent piecemeal toggling to save per-turn tool-call limits.';

const DISABLED_TOOLS_NOTE = DISABLED_TOOLS_NOTE_USER_ONLY;

const GATEWAY_ALL_TOOLS_DESCRIPTION =
  'Returns the latest per-server tool catalog snapshot published by the independent MCP Poll Manager. ' +
  'This operation reads the cache only: it does not probe, initialize, or wait for any upstream server. ' +
  'The catalog includes stable tool_id values, complete inputSchema JSON Schemas, source server information, ' +
  'per-server status, staleness, catalog_revision, compact name-only disabled_tools lists, and disabled_tools_note explaining how to handle disabled capabilities.';

function getDisabledToolsNote(canSelfManage = false) {
  return canSelfManage ? DISABLED_TOOLS_NOTE_SELF_MANAGE : DISABLED_TOOLS_NOTE_USER_ONLY;
}

function formatToolDisabledMessage(targetLabel, serverName = null, canSelfManage = false) {
  const location = serverName ? (' on server "' + serverName + '"') : '';
  const action = canSelfManage
    ? 'Enable it via hotplug_control::manage_tools (batch changes in one call) if needed.'
    : 'Ask the user to enable it in MCP Hotplug if this operation is needed.';
  return 'Tool "' + targetLabel + '"' + location + ' is currently disabled in MCP Hotplug to save tokens. ' + action;
}

function formatDisabledToolsetsFooter(disabledNames, canSelfManage = false) {
  const hint = canSelfManage
    ? 'Use hotplug_control::manage_tools to enable if needed.'
    : 'Ask the user to enable in MCP Hotplug if needed.';
  return '\n\n[Disabled toolsets in MCP Hotplug: ' + disabledNames.join(', ') + '. ' + hint + ']';
}

const ACTION_PARAM_NAMES = ['action', 'command', 'operation', 'subcommand', 'sub_action'];
const TOOLSET_ROOT_SEGMENT = '__toolsets__';
const HOTPLUG_ROOT_SEGMENT = '__hotplug__';

function isPlainObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function isValidInputSchema(schema) {
  if (!isPlainObject(schema)) return false;
  if (schema.type !== undefined && typeof schema.type !== 'string' && !Array.isArray(schema.type)) return false;
  if (schema.properties !== undefined && !isPlainObject(schema.properties)) return false;
  return true;
}

function isExecutableTool(tool) {
  if (!isPlainObject(tool)) return false;
  if (typeof tool.name !== 'string' || !tool.name.trim()) return false;
  if (tool.inputSchema !== undefined && !isValidInputSchema(tool.inputSchema)) return false;
  return true;
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
      const descTool = safeTools.find(t => t && t.name === 'describe_toolset');
      const descProps = isPlainObject(descTool && descTool.inputSchema && descTool.inputSchema.properties)
        ? descTool.inputSchema.properties
        : null;
      let describeParamName = 'toolset_name';
      if (descProps) {
        if ('toolset_name' in descProps) describeParamName = 'toolset_name';
        else if ('toolset' in descProps) describeParamName = 'toolset';
        else if ('name' in descProps) describeParamName = 'name';
        else {
          const firstKey = Object.keys(descProps)[0];
          if (firstKey) describeParamName = firstKey;
        }
      }
      return {
        type: 'toolsets',
        listTool: 'list_toolsets',
        describeTool: 'describe_toolset',
        describeParamName,
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
      const validTools = tsTools.filter(isExecutableTool);
      if (validTools.length === 0) return null;

      const rootBucket = { children: new Map(), leaves: [] };
      for (const t of validTools) {
        const subPath = Array.isArray(t.subPath)
          ? t.subPath.filter(s => typeof s === 'string' && s.trim() && s.trim() !== tsName)
          : [];
        let bucket = rootBucket;
        for (const seg of subPath) {
          if (!bucket.children.has(seg)) bucket.children.set(seg, { children: new Map(), leaves: [] });
          bucket = bucket.children.get(seg);
        }
        bucket.leaves.push(t);
      }

      function buildSubtreeFromBucket(bucket, currentSegments) {
        const nodes = [];
        for (const [subName, childBucket] of bucket.children.entries()) {
          const subSegments = [...currentSegments, subName];
          const subChildren = buildSubtreeFromBucket(childBucket, subSegments);
          if (subChildren.length === 0) continue;
          nodes.push(finalizeCategoryNode({
            path: joinPath(subSegments),
            name: subName,
            displayName: subName,
            kind: 'category',
            metaType: 'subcategory',
            description: '',
            children: subChildren
          }, disabledSet));
        }
        for (const t of bucket.leaves) {
          nodes.push(buildLeafOrSchemaNode(t, currentSegments, disabledSet, 'toolset_tool'));
        }
        nodes.sort((a, b) => {
          if (a.kind !== b.kind) return a.kind === 'category' ? -1 : 1;
          return a.displayName.localeCompare(b.displayName);
        });
        return nodes;
      }

      const childNodes = buildSubtreeFromBucket(rootBucket, tsSegments);
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

    if (toolsetNodes.length === 0) return [];

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
          .filter(isExecutableTool)
          .map(t => buildLeafOrSchemaNode(t, srvSegments, disabledSet, 'hotplug_tool'))
          .sort((a, b) => a.name.localeCompare(b.name));
      }
      if (children.length === 0) return null;

      return finalizeCategoryNode({
        path: joinPath(srvSegments),
        name: srvId,
        displayName: srvName,
        kind: 'category',
        metaType: 'hotplug_server',
        description: '',
        children
      }, disabledSet);
    }).filter(Boolean).sort((a, b) => a.displayName.localeCompare(b.displayName));

    if (serverNodes.length === 0) return [];

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

function getToolsetToolSegments(tsName, toolObj) {
  const subPath = toolObj && Array.isArray(toolObj.subPath)
    ? toolObj.subPath.filter(s => typeof s === 'string' && s.trim() && s.trim() !== tsName)
    : [];
  return [TOOLSET_ROOT_SEGMENT, tsName, ...subPath, String(toolObj.name || '')];
}

function matchToolsetTools(tsObj, requestedTool) {
  const tsTools = tsObj && Array.isArray(tsObj.tools) ? tsObj.tools : [];
  const clean = String(requestedTool || '').trim();
  if (!clean) return [];
  const prefixed = tsObj && tsObj.name ? (tsObj.name + '.' + clean) : '';
  const suffix = '.' + clean;
  return tsTools.filter(t =>
    t && typeof t.name === 'string' &&
    (t.name === clean || (prefixed && t.name === prefixed) || t.name.endsWith(suffix))
  );
}

function checkToolCallAllowed(toolName, args, tools, discoveredNested, disabledList, options = {}) {
  const safeTools = Array.isArray(tools) ? tools : [];
  const disabledSet = createDisabledSet(disabledList);
  const safeArgs = isPlainObject(args) ? args : {};
  const tree = buildServerToolTree(safeTools, discoveredNested, disabledList);
  const canSelfManage = Boolean(options && options.canSelfManage);
  const serverName = options && typeof options.serverName === 'string' ? options.serverName : null;

  const blocked = (targetLabel) => ({
    allowed: false,
    code: 'TOOL_DISABLED',
    message: formatToolDisabledMessage(targetLabel, serverName, canSelfManage)
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

    if (toolName === metaPattern.describeTool) {
      const descParam = metaPattern.describeParamName || 'toolset_name';
      const rawRequested = safeArgs[descParam] ?? safeArgs.toolset_name ?? safeArgs.toolset ?? safeArgs.name;
      const requestedToolset = typeof rawRequested === 'string' ? rawRequested.trim() : '';
      if (requestedToolset) {
        const tsPath = joinPath([TOOLSET_ROOT_SEGMENT, requestedToolset]);
        const tsObj = isPlainObject(discoveredNested) && Array.isArray(discoveredNested.toolsets)
          ? discoveredNested.toolsets.find(t => t && t.name === requestedToolset)
          : null;
        const tsTools = tsObj && Array.isArray(tsObj.tools) ? tsObj.tools : [];
        const allChildrenDisabled = tsTools.length > 0 && tsTools.every(t =>
          isPathDisabled(joinPath(getToolsetToolSegments(requestedToolset, t)), disabledSet)
        );
        if (isPathDisabled(tsPath, disabledSet) || allChildrenDisabled) {
          return blocked(requestedToolset);
        }
      }
    }

    if (toolName === metaPattern.callTool) {
      const requestedToolset = typeof safeArgs.toolset_name === 'string' ? safeArgs.toolset_name.trim() : '';
      const requestedTool = typeof safeArgs.tool_name === 'string'
        ? safeArgs.tool_name.trim()
        : (typeof safeArgs.name === 'string' ? safeArgs.name.trim() : '');
      const nestedArgs = isPlainObject(safeArgs.arguments) ? safeArgs.arguments : {};

      const isLeafOrActionDisabled = (segments) => {
        if (isPathDisabled(joinPath(segments), disabledSet)) return true;
        for (const actionParam of ACTION_PARAM_NAMES) {
          if (typeof nestedArgs[actionParam] === 'string' && nestedArgs[actionParam].trim()) {
            if (isPathDisabled(joinPath([...segments, nestedArgs[actionParam].trim()]), disabledSet)) {
              return true;
            }
          }
        }
        return false;
      };

      if (requestedToolset) {
        const tsPath = joinPath([TOOLSET_ROOT_SEGMENT, requestedToolset]);
        if (isPathDisabled(tsPath, disabledSet)) {
          return blocked(requestedToolset + '/' + (requestedTool || '*'));
        }
        if (requestedTool) {
          if (isLeafOrActionDisabled([TOOLSET_ROOT_SEGMENT, requestedToolset, requestedTool])) {
            return blocked(requestedToolset + '/' + requestedTool);
          }
          const tsObj = isPlainObject(discoveredNested) && Array.isArray(discoveredNested.toolsets)
            ? discoveredNested.toolsets.find(t => t && t.name === requestedToolset)
            : null;
          if (tsObj) {
            const matched = matchToolsetTools(tsObj, requestedTool);
            if (matched.length > 0 && matched.every(t => isLeafOrActionDisabled(getToolsetToolSegments(requestedToolset, t)))) {
              return blocked(requestedToolset + '/' + requestedTool);
            }
          }
        }
      } else if (requestedTool && isPlainObject(discoveredNested) && Array.isArray(discoveredNested.toolsets)) {
        // toolset_name is optional in Unreal call_tool; resolve against discovered toolsets.
        const matches = [];
        for (const ts of discoveredNested.toolsets) {
          if (ts.isSingleTool === true && ts.name === requestedTool) {
            matches.push({ tsName: ts.name, segments: [TOOLSET_ROOT_SEGMENT, ts.name] });
            continue;
          }
          const matchedTools = matchToolsetTools(ts, requestedTool);
          for (const t of matchedTools) {
            matches.push({ tsName: ts.name, segments: getToolsetToolSegments(ts.name, t) });
          }
        }
        if (matches.length > 0) {
          if (matches.every(m => isLeafOrActionDisabled(m.segments))) {
            return blocked(matches[0].tsName + '/' + requestedTool);
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

const SKIP_TRAVERSE_KEYS = new Set([
  'inputSchema', 'input_schema', 'outputSchema', 'output_schema',
  'parameters', 'annotations', '_meta', 'disabled_tools', 'disabled_tools_note',
  'disabled_toolsets', 'schema', '$schema', 'tags', 'examples', 'required'
]);
const TRANSPARENT_WRAPPER_KEYS = new Set([
  'result', 'data', 'toolset', 'tools', 'items', 'methods',
  'actions', 'functions', 'definitions', 'children', 'categories',
  'groups', 'subcategories', 'entries', 'list'
]);
const TOOL_CONTAINER_KEYS = new Set([
  'tools', 'children', 'methods', 'functions', 'definitions', 'subtools', 'items', 'entries', 'list'
]);

function getToolInputSchema(obj) {
  if (!isPlainObject(obj)) return null;
  if (isPlainObject(obj.inputSchema)) return obj.inputSchema;
  if (isPlainObject(obj.input_schema)) return obj.input_schema;
  if (isPlainObject(obj.parameters)) return obj.parameters;
  return null;
}

function hasNestedToolObjects(obj) {
  for (const key of TOOL_CONTAINER_KEYS) {
    const val = obj[key];
    if (Array.isArray(val) && val.some(isPlainObject)) return true;
    if (isPlainObject(val) && Object.values(val).some(isPlainObject)) return true;
  }
  return false;
}

function parseToolsetToolsFromData(data, rawTextFallback = '', rootName = '') {
  const result = [];
  const seen = new Set();

  function addTool(raw, subPath = [], fallbackName = '') {
    if (typeof raw === 'string') {
      const clean = raw.trim();
      if (!clean || seen.has(clean)) return;
      seen.add(clean);
      const cleanSubPath = subPath.filter(s => s && s !== rootName && s !== clean);
      const entry = { name: clean, description: '', inputSchema: {} };
      if (cleanSubPath.length > 0) entry.subPath = cleanSubPath;
      result.push(entry);
      return;
    }
    if (!isPlainObject(raw)) return;
    const name = String(raw.name || raw.tool_name || raw.id || fallbackName || '').trim();
    if (!name || seen.has(name)) return;
    const schema = getToolInputSchema(raw);
    seen.add(name);
    const cleanSubPath = subPath.filter(s => s && s !== rootName && s !== name);
    const entry = {
      name,
      description: String(raw.description || ''),
      inputSchema: schema || {}
    };
    if (cleanSubPath.length > 0) entry.subPath = cleanSubPath;
    result.push(entry);
  }

  function walk(node, ancestorPath, depth) {
    if (depth > 16 || node === null || node === undefined) return;

    if (Array.isArray(node)) {
      for (const item of node) {
        if (isPlainObject(item) || Array.isArray(item)) {
          walk(item, ancestorPath, depth + 1);
        }
      }
      return;
    }

    if (!isPlainObject(node)) return;

    const schema = getToolInputSchema(node);
    const ownName = String(node.name || node.tool_name || node.id || '').trim();
    const containsNestedTools = hasNestedToolObjects(node);

    // Bottom-up leaf rule: a named object with a valid inputSchema and no nested tool objects is a leaf tool.
    if (ownName && schema && !containsNestedTools) {
      addTool(node, ancestorPath);
      return;
    }

    const nextBasePath = (ownName && ownName !== rootName && ancestorPath[ancestorPath.length - 1] !== ownName)
      ? [...ancestorPath, ownName]
      : ancestorPath;

    const beforeCount = result.length;
    for (const [key, val] of Object.entries(node)) {
      if (SKIP_TRAVERSE_KEYS.has(key)) continue;
      if (!val || typeof val !== 'object') continue;
      const nextPath = TRANSPARENT_WRAPPER_KEYS.has(key)
        ? nextBasePath
        : [...nextBasePath, key];
      if (isPlainObject(val) && getToolInputSchema(val) && !val.name && !val.tool_name && !val.id) {
        addTool(val, nextBasePath, key);
      } else {
        walk(val, nextPath, depth + 1);
      }
    }

    if (result.length === beforeCount && ownName && schema) {
      addTool(node, ancestorPath);
    }
  }

  if (data !== null && data !== undefined) {
    walk(data, [], 0);
    // Fallback for name-only JSON tool lists (e.g. { tools: ["foo", "bar"] } or ["foo", "bar"])
    if (result.length === 0) {
      let stringList = null;
      if (Array.isArray(data) && data.every(x => typeof x === 'string')) {
        stringList = data;
      } else if (isPlainObject(data)) {
        const container = isPlainObject(data.toolset) ? data.toolset : (isPlainObject(data.data) ? data.data : (isPlainObject(data.result) ? data.result : data));
        for (const key of TOOL_CONTAINER_KEYS) {
          if (Array.isArray(container[key]) && container[key].length > 0 && container[key].every(x => typeof x === 'string')) {
            stringList = container[key];
            break;
          }
        }
      }
      if (stringList) {
        for (const s of stringList) addTool(s, []);
      }
    }
  } else if (rawTextFallback) {
    for (const line of String(rawTextFallback).split(/\r?\n/)) {
      const m = line.match(/^\s*(?:[-*+]|\d+\.|##+)\s+`?([A-Za-z0-9_.:-]+)`?(?:\s*[:\-–—]\s*(.*))?$/);
      if (m && !['Tools', 'Toolset', 'Parameters', 'Description', 'Schema'].includes(m[1])) {
        addTool({ name: m[1], description: m[2] || '' });
      }
    }
  }

  return result;
}

function filterToolCallResponse(toolName, args, result, tools, discoveredNested, disabledList, options = {}) {
  if (!isPlainObject(result) || result.isError === true) return result;
  const safeTools = Array.isArray(tools) ? tools : [];
  const metaPattern = detectMetaPattern(safeTools);
  if (!metaPattern) return result;

  const disabledSet = createDisabledSet(disabledList);
  const safeArgs = isPlainObject(args) ? args : {};
  const extracted = extractJsonFromToolResult(result);
  const canSelfManage = Boolean(options && options.canSelfManage);
  const disabledNote = getDisabledToolsNote(canSelfManage);

  if (metaPattern.type === 'toolsets' && toolName === metaPattern.listTool) {
    const knownToolsetNames = new Set(
      isPlainObject(discoveredNested) && Array.isArray(discoveredNested.toolsets)
        ? discoveredNested.toolsets.map(t => t && String(t.name || '').trim()).filter(Boolean)
        : []
    );

    const isToolsetDisabled = (tsName) => {
      const tsPath = joinPath([TOOLSET_ROOT_SEGMENT, tsName]);
      const tsObj = isPlainObject(discoveredNested) && Array.isArray(discoveredNested.toolsets)
        ? discoveredNested.toolsets.find(t => t && t.name === tsName)
        : null;
      const tsTools = tsObj && Array.isArray(tsObj.tools) ? tsObj.tools : [];
      const allChildrenDisabled = tsTools.length > 0 && tsTools.every(t =>
        isPathDisabled(joinPath(getToolsetToolSegments(tsName, t)), disabledSet)
      );
      if (isPathDisabled(tsPath, disabledSet) || allChildrenDisabled) {
        return { disabled: true, tsTools };
      }
      return { disabled: false, tsTools };
    };

    if (!extracted) {
      const content = Array.isArray(result.content) ? result.content : [];
      const textIdx = content.findIndex(b => b && b.type === 'text' && typeof b.text === 'string' && b.text.trim());
      if (textIdx < 0) return result;
      const rawText = content[textIdx].text;
      const lines = rawText.split(/\r?\n/);
      const keptLines = [];
      const disabledNames = [];
      let skippingCurrent = false;
      let matchedAny = false;

      for (const line of lines) {
        const m = line.match(/^\s{0,1}(?:[-*+]|\d+\.)\s+`?([A-Za-z0-9_.:-]+)`?(?:\s*[:\-–—]\s*(.*))?$/);
        const candidateName = m ? m[1].trim() : '';
        const isKnownHeader = Boolean(
          candidateName &&
          (knownToolsetNames.size === 0 ||
            knownToolsetNames.has(candidateName) ||
            disabledSet.has(joinPath([TOOLSET_ROOT_SEGMENT, candidateName])))
        );
        if (isKnownHeader) {
          matchedAny = true;
          const check = isToolsetDisabled(candidateName);
          if (check.disabled) {
            skippingCurrent = true;
            disabledNames.push(candidateName);
          } else {
            skippingCurrent = false;
            keptLines.push(line);
          }
        } else if (!skippingCurrent) {
          keptLines.push(line);
        }
      }

      if (!matchedAny || disabledNames.length === 0) return result;
      const copy = cloneJson(result);
      const footer = formatDisabledToolsetsFooter(disabledNames, canSelfManage);
      copy.content[textIdx] = {
        ...copy.content[textIdx],
        type: 'text',
        text: keptLines.join('\n').trim() + footer
      };
      return copy;
    }

    const data = extracted.data;
    const disabledToolsets = [];
    const filterEntry = (entry) => {
      const tsName = typeof entry === 'string'
        ? entry
        : (isPlainObject(entry) ? String(entry.name || entry.toolset_name || entry.id || '') : '');
      if (!tsName) return true;
      const check = isToolsetDisabled(tsName);
      if (check.disabled) {
        if (check.tsTools.length > 0) {
          disabledToolsets.push({ [tsName]: check.tsTools.map(t => t.name) });
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
        disabled_tools_note: disabledNote
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
      filteredPayload.disabled_tools_note = disabledNote;
    } else {
      return result;
    }

    return writeFilteredJsonToToolResult(result, extracted, filteredPayload);
  }

  if (!extracted) return result;

  if (metaPattern.type === 'toolsets' && toolName === metaPattern.describeTool) {
    const descParam = metaPattern.describeParamName || 'toolset_name';
    const rawTs = safeArgs[descParam] ?? safeArgs.toolset_name ?? safeArgs.toolset ?? safeArgs.name;
    const tsName = typeof rawTs === 'string' ? rawTs.trim() : '';
    if (!tsName) return result;

    const tsObj = isPlainObject(discoveredNested) && Array.isArray(discoveredNested.toolsets)
      ? discoveredNested.toolsets.find(t => t && t.name === tsName)
      : null;

    const data = extracted.data;
    const disabledNames = [];
    const filterToolItem = (item) => {
      const name = typeof item === 'string'
        ? item
        : (isPlainObject(item) ? String(item.name || item.tool_name || item.id || '') : '');
      if (!name) return item;
      const matched = tsObj ? matchToolsetTools(tsObj, name)[0] : null;
      const leafPath = matched
        ? joinPath(getToolsetToolSegments(tsName, matched))
        : joinPath([TOOLSET_ROOT_SEGMENT, tsName, name]);
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
        disabled_tools_note: disabledNote
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
      filteredPayload.disabled_tools_note = disabledNote;
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
    filteredPayload.disabled_tools_note = disabledNote;
    return writeFilteredJsonToToolResult(result, extracted, filteredPayload);
  }

  return result;
}

function isTransientDiscoveryError(text) {
  return /timed out|etimedout|econnrefused|econnreset|ehostunreach|enetunreach|socket hang up|socket.*closed|broken pipe|upstream.*unavailable|temporarily unavailable|http\s+50[234]\b/i.test(String(text || ''));
}

async function discoverNestedForServer(callToolFn, tools, depth = 0, maxDepth = 5) {
  if (depth >= maxDepth) return null;
  const metaPattern = detectMetaPattern(tools);
  if (!metaPattern) return null;

  if (metaPattern.type === 'toolsets') {
    const listRes = await callToolFn(metaPattern.listTool, {});
    if (listRes && listRes.isError === true) {
      throw new Error('list_toolsets returned an error during nested tool discovery.');
    }
    const extractedList = extractJsonFromToolResult(listRes);
    const rawText = Array.isArray(listRes && listRes.content)
      ? listRes.content.filter(b => b && b.type === 'text').map(b => String(b.text || '')).join('\n')
      : '';
    const toolsets = parseToolsetsListFromData(extractedList ? extractedList.data : null, rawText);
    if (toolsets.length === 0) {
      return {
        pattern: 'toolsets',
        schemaVersion: 4,
        discoveredAt: new Date().toISOString(),
        toolsets: []
      };
    }

    const descParam = metaPattern.describeParamName || 'toolset_name';
    const detailed = [];
    const failedToolsets = [];
    const batchSize = 4;
    for (let i = 0; i < toolsets.length; i += batchSize) {
      const slice = toolsets.slice(i, i + batchSize);
      const batchResults = await Promise.all(slice.map(async ts => {
        let lastError = null;
        for (let attempt = 0; attempt < 3; attempt++) {
          try {
            const descRes = await callToolFn(metaPattern.describeTool, { [descParam]: ts.name });
            const rawDescText = Array.isArray(descRes && descRes.content)
              ? descRes.content.filter(b => b && b.type === 'text').map(b => String(b.text || '')).join('\n')
              : '';
            if (descRes && descRes.isError === true) {
              // Transient transport/server overload errors are retried and abort incomplete discovery;
              // clean application-level rejections (e.g. non-existent candidate parsed from Markdown) are skipped.
              if (isTransientDiscoveryError(rawDescText)) {
                lastError = new Error(rawDescText || ('describe_toolset transient error for ' + ts.name));
                if (attempt < 2) {
                  await new Promise(r => setTimeout(r, 250 * (attempt + 1)));
                  continue;
                }
                return { failed: ts.name, error: lastError };
              }
              return { skipped: true };
            }
            const extractedDesc = extractJsonFromToolResult(descRes);
            const tsTools = parseToolsetToolsFromData(extractedDesc ? extractedDesc.data : null, rawDescText, ts.name)
              .filter(isExecutableTool);
            if (tsTools.length === 1 && tsTools[0].name === ts.name) {
              return { item: { name: ts.name, description: tsTools[0].description || ts.description, isSingleTool: true, inputSchema: tsTools[0].inputSchema, tools: [] } };
            }
            if (tsTools.length === 0) return { skipped: true };
            return { item: { name: ts.name, description: ts.description, tools: tsTools } };
          } catch (err) {
            const msg = String(err instanceof Error ? err.message : err);
            if (/JSON-RPC\s+-3260[12]\b/i.test(msg)) {
              return { skipped: true };
            }
            lastError = err;
            if (attempt < 2) {
              await new Promise(r => setTimeout(r, 250 * (attempt + 1)));
              continue;
            }
            return { failed: ts.name, error: lastError };
          }
        }
        return { failed: ts.name, error: lastError };
      }));
      for (const entry of batchResults) {
        if (!entry) continue;
        if (entry.failed) failedToolsets.push(entry.failed);
        else if (entry.item) detailed.push(entry.item);
      }
    }

    if (failedToolsets.length > 0) {
      throw new Error('Incomplete nested toolset discovery; failed toolsets: ' + failedToolsets.join(', '));
    }

    return {
      pattern: 'toolsets',
      schemaVersion: 4,
      discoveredAt: new Date().toISOString(),
      toolsets: detailed
    };
  }

  if (metaPattern.type === 'hotplug') {
    const catalogRes = await callToolFn('all_tools', {});
    if (catalogRes && catalogRes.isError === true) {
      throw new Error('Nested all_tools returned an error during discovery.');
    }
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
      const candidateTool = {
        name: String(tool.name || ''),
        description: String(tool.description || ''),
        inputSchema: isPlainObject(tool.inputSchema) ? tool.inputSchema : {}
      };
      if (isExecutableTool(candidateTool)) {
        serversMap.get(srvId).tools.push(candidateTool);
      }
    }

    const servers = [...serversMap.values()];
    const catalogRevision = String(data.catalog_revision || '');
    for (const srv of servers) {
      const subPattern = detectMetaPattern(srv.tools);
      if (subPattern && depth + 1 < maxDepth) {
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
      }
    }

    return {
      pattern: 'hotplug',
      schemaVersion: 4,
      discoveredAt: new Date().toISOString(),
      servers
    };
  }

  return null;
}

function indexServerToolTree(tree) {
  const leafEnabledMap = new Map();
  const leafByCanonical = new Map();
  const categoryByFullName = new Map();
  const topCategories = [];
  const flatLeaves = [];

  function formatLeafCanonicalName(node, ctx) {
    if (node.metaType === 'schema_action' && ctx.schemaRootCanonical) {
      return ctx.schemaRootCanonical + '/' + node.name;
    }
    if (ctx.toolsetName) {
      if (node.name === ctx.toolsetName || node.name.startsWith(ctx.toolsetName + '.')) {
        return node.name;
      }
      return ctx.toolsetName + '/' + node.name;
    }
    if (ctx.hotplugServerName) {
      return ctx.hotplugServerName + '/' + node.name;
    }
    return node.name;
  }

  function registerLeaf(node, ctx, targetLeafList) {
    const canonicalName = formatLeafCanonicalName(node, ctx);
    const leafRecord = {
      path: node.path,
      name: node.name,
      canonicalName,
      enabled: Boolean(node.enabled),
      categoryName: ctx.topCategoryName || null
    };
    leafEnabledMap.set(node.path, leafRecord.enabled);
    if (!leafByCanonical.has(canonicalName)) {
      leafByCanonical.set(canonicalName, leafRecord);
    }
    if (Array.isArray(targetLeafList)) {
      targetLeafList.push(leafRecord);
    }
    return leafRecord;
  }

  function collectSubtreeLeaves(node, ctx, outLeaves) {
    if (node.kind === 'tool') {
      registerLeaf(node, ctx, outLeaves);
      return;
    }

    const nextCtx = { ...ctx };
    if (node.metaType === 'schema_root') {
      nextCtx.schemaRootCanonical = formatLeafCanonicalName(node, ctx);
    }

    const children = Array.isArray(node.children) ? node.children : [];
    if (node.metaType === 'subcategory') {
      const subFullName = ctx.categoryPrefix ? (ctx.categoryPrefix + '/' + node.name) : node.name;
      nextCtx.categoryPrefix = subFullName;
      const subLeaves = [];
      for (const child of children) {
        collectSubtreeLeaves(child, nextCtx, subLeaves);
      }
      for (const leaf of subLeaves) outLeaves.push(leaf);
      if (!categoryByFullName.has(subFullName)) {
        categoryByFullName.set(subFullName, {
          name: subFullName,
          node,
          leaves: subLeaves,
          isTopLevel: false
        });
      }
      return;
    }

    for (const child of children) {
      collectSubtreeLeaves(child, nextCtx, outLeaves);
    }
  }

  function processTopNode(node, isFromToolsetRoot = false, isFromHotplugRoot = false) {
    if (node.metaType === 'toolset_root') {
      for (const child of node.children || []) {
        processTopNode(child, true, false);
      }
      return;
    }
    if (node.metaType === 'hotplug_root') {
      for (const child of node.children || []) {
        processTopNode(child, false, true);
      }
      return;
    }

    if (node.kind === 'category') {
      const catName = node.name;
      const leaves = [];
      const ctx = {
        topCategoryName: catName,
        categoryPrefix: catName,
        toolsetName: node.metaType === 'toolset' ? catName : null,
        hotplugServerName: node.metaType === 'hotplug_server' ? catName : null,
        schemaRootCanonical: node.metaType === 'schema_root' ? catName : null
      };
      for (const child of node.children || []) {
        collectSubtreeLeaves(child, ctx, leaves);
      }
      const catRecord = { name: catName, node, leaves, isTopLevel: true };
      categoryByFullName.set(catName, catRecord);
      topCategories.push(catRecord);
      return;
    }

    if (isFromToolsetRoot) {
      // Single-tool toolset (isSingleTool === true): expose both as category and leaf tool.
      const catName = node.name;
      const leaves = [];
      registerLeaf(node, { topCategoryName: catName, toolsetName: catName }, leaves);
      const catRecord = { name: catName, node, leaves, isTopLevel: true };
      categoryByFullName.set(catName, catRecord);
      topCategories.push(catRecord);
      return;
    }

    const leafCtx = {
      topCategoryName: null,
      hotplugServerName: isFromHotplugRoot ? node.name : null
    };
    registerLeaf(node, leafCtx, flatLeaves);
  }

  for (const root of (tree && tree.nodes) || []) {
    processTopNode(root, false, false);
  }

  return {
    leafEnabledMap,
    leafByCanonical,
    categoryByFullName,
    topCategories,
    flatLeaves
  };
}

function collapseDisabledPaths(tree, leafEnabledMap) {
  const result = [];

  function visit(node) {
    if (node.kind === 'tool') {
      const isEnabled = leafEnabledMap.has(node.path) ? Boolean(leafEnabledMap.get(node.path)) : Boolean(node.enabled);
      if (!isEnabled) result.push(node.path);
      return isEnabled ? 'all' : 'none';
    }

    const children = Array.isArray(node.children) ? node.children : [];
    if (children.length === 0) {
      const isEnabled = leafEnabledMap.has(node.path) ? Boolean(leafEnabledMap.get(node.path)) : Boolean(node.enabled);
      if (!isEnabled) result.push(node.path);
      return isEnabled ? 'all' : 'none';
    }

    const startIdx = result.length;
    let allEnabled = true;
    let allDisabled = true;
    for (const child of children) {
      const childState = visit(child);
      if (childState !== 'all') allEnabled = false;
      if (childState !== 'none') allDisabled = false;
    }

    if (allDisabled) {
      result.length = startIdx;
      result.push(node.path);
      return 'none';
    }
    return allEnabled ? 'all' : 'partial';
  }

  for (const root of (tree && tree.nodes) || []) {
    visit(root);
  }

  return normalizeDisabledList(result);
}

function summarizeCategoryRecord(catRecord) {
  let enabled = 0;
  const total = catRecord.leaves.length;
  for (const leaf of catRecord.leaves) {
    if (leaf.enabled) enabled += 1;
  }
  const status = total === 0
    ? (catRecord.node.enabled ? 'enabled' : 'disabled')
    : (enabled === total ? 'enabled' : (enabled === 0 ? 'disabled' : 'partial'));
  return { name: catRecord.name, status, enabled, total };
}

function inspectServerToolTree(tree, options = {}) {
  const index = indexServerToolTree(tree);
  const statusFilter = options.status === 'enabled' || options.status === 'disabled' ? options.status : 'all';
  const searchRaw = typeof options.search === 'string' ? options.search.trim().toLowerCase() : '';
  const requestedCategories = Array.isArray(options.categories)
    ? [...new Set(options.categories.filter(c => typeof c === 'string' && c.trim()).map(c => c.trim()))]
    : [];

  const leafMatchesStatus = (leaf) =>
    statusFilter === 'all' || (statusFilter === 'enabled' ? leaf.enabled : !leaf.enabled);
  const categoryMatchesStatus = (summary) =>
    statusFilter === 'all' ||
    (statusFilter === 'enabled' ? summary.enabled > 0 : summary.enabled < summary.total);

  if (requestedCategories.length > 0) {
    const categoriesResult = {};
    const notFound = [];
    for (const catName of requestedCategories) {
      const cat = index.categoryByFullName.get(catName);
      if (!cat) {
        notFound.push(catName);
        continue;
      }
      const enabledList = [];
      const disabledList = [];
      for (const leaf of cat.leaves) {
        if (!leafMatchesStatus(leaf)) continue;
        if (searchRaw && !leaf.canonicalName.toLowerCase().includes(searchRaw)) continue;
        if (leaf.enabled) enabledList.push(leaf.canonicalName);
        else disabledList.push(leaf.canonicalName);
      }
      categoriesResult[catName] = {
        ...(statusFilter !== 'disabled' ? { enabled: enabledList } : {}),
        ...(statusFilter !== 'enabled' ? { disabled: disabledList } : {})
      };
    }
    return {
      has_categories: true,
      categories: categoriesResult,
      ...(notFound.length > 0 ? { not_found: notFound } : {})
    };
  }

  if (searchRaw) {
    const matchedCategories = [];
    for (const cat of index.topCategories) {
      if (!cat.name.toLowerCase().includes(searchRaw)) continue;
      const summary = summarizeCategoryRecord(cat);
      if (categoryMatchesStatus(summary)) matchedCategories.push(summary);
    }
    const enabledTools = [];
    const disabledTools = [];
    for (const leaf of index.leafByCanonical.values()) {
      if (!leafMatchesStatus(leaf)) continue;
      if (!leaf.canonicalName.toLowerCase().includes(searchRaw)) continue;
      if (leaf.enabled) enabledTools.push(leaf.canonicalName);
      else disabledTools.push(leaf.canonicalName);
    }
    return {
      has_categories: index.topCategories.length > 0,
      ...(matchedCategories.length > 0 ? { matched_categories: matchedCategories } : {}),
      ...(statusFilter !== 'disabled' ? { enabled_tools: enabledTools } : {}),
      ...(statusFilter !== 'enabled' ? { disabled_tools: disabledTools } : {})
    };
  }

  if (index.topCategories.length > 0) {
    const categories = index.topCategories
      .map(summarizeCategoryRecord)
      .filter(categoryMatchesStatus);
    const result = {
      has_categories: true,
      categories
    };
    if (index.flatLeaves.length > 0) {
      const enabledFlat = index.flatLeaves.filter(l => l.enabled && leafMatchesStatus(l)).map(l => l.canonicalName);
      const disabledFlat = index.flatLeaves.filter(l => !l.enabled && leafMatchesStatus(l)).map(l => l.canonicalName);
      if (enabledFlat.length > 0 || disabledFlat.length > 0) {
        result.flat_tools = {
          ...(statusFilter !== 'disabled' ? { enabled: enabledFlat } : {}),
          ...(statusFilter !== 'enabled' ? { disabled: disabledFlat } : {})
        };
      }
    }
    return result;
  }

  const enabledTools = index.flatLeaves.filter(l => l.enabled && leafMatchesStatus(l)).map(l => l.canonicalName);
  const disabledTools = index.flatLeaves.filter(l => !l.enabled && leafMatchesStatus(l)).map(l => l.canonicalName);
  return {
    has_categories: false,
    ...(statusFilter !== 'disabled' ? { enabled_tools: enabledTools } : {}),
    ...(statusFilter !== 'enabled' ? { disabled_tools: disabledTools } : {})
  };
}

function applyServerToolMutations(tree, mutation) {
  const index = indexServerToolTree(tree);
  const initialMap = new Map(index.leafEnabledMap);
  const nextMap = new Map(index.leafEnabledMap);
  const notFound = [];

  const setCategoryLeaves = (cat, enabled) => {
    for (const leaf of cat.leaves) {
      nextMap.set(leaf.path, enabled);
    }
  };

  if (mutation.reset_all === 'enable') {
    for (const leafPath of nextMap.keys()) nextMap.set(leafPath, true);
  } else if (mutation.reset_all === 'disable') {
    for (const leafPath of nextMap.keys()) nextMap.set(leafPath, false);
  }

  if (Array.isArray(mutation.keep_only_categories)) {
    const matched = [];
    for (const rawName of mutation.keep_only_categories) {
      if (typeof rawName !== 'string' || !rawName.trim()) continue;
      const name = rawName.trim();
      const cat = index.categoryByFullName.get(name);
      if (cat) matched.push(cat);
      else notFound.push(name);
    }
    if (matched.length > 0 || mutation.keep_only_categories.length === 0) {
      for (const leafPath of nextMap.keys()) nextMap.set(leafPath, false);
      for (const cat of matched) setCategoryLeaves(cat, true);
    }
  }

  if (Array.isArray(mutation.disable_categories)) {
    for (const rawName of mutation.disable_categories) {
      if (typeof rawName !== 'string' || !rawName.trim()) continue;
      const name = rawName.trim();
      const cat = index.categoryByFullName.get(name);
      if (cat) setCategoryLeaves(cat, false);
      else notFound.push(name);
    }
  }

  if (Array.isArray(mutation.enable_categories)) {
    for (const rawName of mutation.enable_categories) {
      if (typeof rawName !== 'string' || !rawName.trim()) continue;
      const name = rawName.trim();
      const cat = index.categoryByFullName.get(name);
      if (cat) setCategoryLeaves(cat, true);
      else notFound.push(name);
    }
  }

  if (Array.isArray(mutation.keep_only_tools)) {
    const matched = [];
    for (const rawName of mutation.keep_only_tools) {
      if (typeof rawName !== 'string' || !rawName.trim()) continue;
      const name = rawName.trim();
      const leaf = index.leafByCanonical.get(name);
      if (leaf) matched.push(leaf);
      else notFound.push(name);
    }
    if (matched.length > 0 || mutation.keep_only_tools.length === 0) {
      for (const leafPath of nextMap.keys()) nextMap.set(leafPath, false);
      for (const leaf of matched) nextMap.set(leaf.path, true);
    }
  } else if (isPlainObject(mutation.keep_only_tools)) {
    for (const [rawCatName, rawToolNames] of Object.entries(mutation.keep_only_tools)) {
      const catName = String(rawCatName || '').trim();
      const cat = index.categoryByFullName.get(catName);
      if (!cat) {
        notFound.push(catName);
        continue;
      }
      const list = Array.isArray(rawToolNames) ? rawToolNames : [];
      const catLeafByCanonical = new Map(cat.leaves.map(l => [l.canonicalName, l]));
      const catLeafByName = new Map(cat.leaves.map(l => [l.name, l]));
      const matched = [];
      for (const rawTool of list) {
        if (typeof rawTool !== 'string' || !rawTool.trim()) continue;
        const toolName = rawTool.trim();
        const leaf = catLeafByCanonical.get(toolName) || catLeafByName.get(toolName);
        if (leaf) matched.push(leaf);
        else notFound.push(toolName);
      }
      if (matched.length > 0 || list.length === 0) {
        setCategoryLeaves(cat, false);
        for (const leaf of matched) nextMap.set(leaf.path, true);
      }
    }
  }

  if (Array.isArray(mutation.disable_tools)) {
    for (const rawName of mutation.disable_tools) {
      if (typeof rawName !== 'string' || !rawName.trim()) continue;
      const name = rawName.trim();
      const leaf = index.leafByCanonical.get(name);
      if (leaf) nextMap.set(leaf.path, false);
      else notFound.push(name);
    }
  }

  if (Array.isArray(mutation.enable_tools)) {
    for (const rawName of mutation.enable_tools) {
      if (typeof rawName !== 'string' || !rawName.trim()) continue;
      const name = rawName.trim();
      const leaf = index.leafByCanonical.get(name);
      if (leaf) nextMap.set(leaf.path, true);
      else notFound.push(name);
    }
  }

  let changedCount = 0;
  let enabledCount = 0;
  const totalCount = nextMap.size;
  for (const [leafPath, isEnabled] of nextMap.entries()) {
    if (isEnabled) enabledCount += 1;
    if (initialMap.get(leafPath) !== isEnabled) changedCount += 1;
  }

  const disabledTools = collapseDisabledPaths(tree, nextMap);
  return {
    disabledTools,
    changedCount,
    enabledCount,
    totalCount,
    notFound: [...new Set(notFound)]
  };
}

module.exports = {
  HOTPLUG_CONTROL_SERVER_ID,
  HOTPLUG_CONTROL_MANAGE_TOOL,
  HOTPLUG_CONTROL_LIST_TOOL,
  DISABLED_TOOLS_NOTE,
  DISABLED_TOOLS_NOTE_USER_ONLY,
  DISABLED_TOOLS_NOTE_SELF_MANAGE,
  GATEWAY_ALL_TOOLS_DESCRIPTION,
  TOOLSET_ROOT_SEGMENT,
  HOTPLUG_ROOT_SEGMENT,
  getDisabledToolsNote,
  formatToolDisabledMessage,
  formatDisabledToolsetsFooter,
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
  collapseDisabledPaths,
  inspectServerToolTree,
  applyServerToolMutations,
  filterCatalogToolsForServer,
  checkToolCallAllowed,
  filterToolCallResponse,
  discoverNestedForServer
};