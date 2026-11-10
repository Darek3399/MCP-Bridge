# Architecture

## Application lifetime

- A single normal-user WPF process (`MCPBridge.exe`, targeting `.NET Framework 4.8` on `win-x64`) owns the manager UI and every MCP child process it starts.
- Optional per-user startup uses `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- Closing the window minimizes the application to the Windows system tray. The explicit tray **Exit** action stops all managed child processes and releases reserved ports.
- When an external local application (such as Unreal Editor, Blender, or an IDE) closes, only its corresponding MCP upstream transitions to `Waiting` or `Offline`; the local MCP pipeline (`Dynamic Router`, `Bridge MCP Gateway`, `Filesystem MCP`, and other active servers) continues running uninterrupted.

## Local MCP pipeline & Meta-Tool Gateway

| Component | Local endpoint | Purpose |
|---|---|---|
| Bridge MCP Gateway (external) | `127.0.0.1:8004/mcp` | Stable MCP contract exposed to AI clients; permanently publishes only `all_tools` and `call_tool` |
| Dynamic Router & Poll Manager (internal) | `127.0.0.1:8001/mcp` | Manages per-server workers, caches tool schemas, routes `tools/call`, and strips client `roots` |
| Filesystem MCP gateway | `127.0.0.1:8003/filesystem/mcp` | Built-in STDIO-to-HTTP Filesystem server restricted by the manager-owned roots allow-list |
| Custom STDIO gateways | `127.0.0.1:8010–8089/<id>/mcp` | On-demand `Supergateway` instances wrapping enabled CLI (`npx`, `uvx`, `python`, `.exe`) servers |
| Built-in Unreal MCP template | `127.0.0.1:8000/mcp` | Pre-configured (disabled by default) HTTP MCP template for Unreal Engine (`:8000`) |

All local listeners bind exclusively to `127.0.0.1`. Tailscale Funnel publishes the Bridge MCP Gateway on port `8004` (`/mcp`) only when online access is explicitly enabled and both the internal Router and external Gateway are ready.

### Why two meta-tools (`all_tools` + `call_tool`)?

1. **Zero Context Bloat:** Instead of injecting 50–150+ full JSON tool schemas into the AI client's system prompt at the start of a conversation, `Bridge MCP Gateway` (`Adapters/bridge-mcp-gateway.js`) exposes only two lightweight tools: `all_tools` and `call_tool`.
2. **Real-Time Hot-Swapping Across All Clients:** Many desktop and web AI clients cache the initial `tools/list` response and ignore `notifications/tools/list_changed`. Because `all_tools` reads the live `McpServerStateCache` snapshot (`catalog_revision` / `cache_revision`) maintained by the internal Router, toggling servers or switching presets in the Bridge UI takes effect immediately mid-conversation without restarting the chat or refreshing the client.
3. **Schema Validation & Revision Safety:** `BridgeToolDispatcher` (`Adapters/bridge-tool-dispatcher.js`) validates tool arguments against the server's cached JSON Schema (`Adapters/bridge-schema-validator.js`) and verifies the `server_revision` token immediately before dispatch so stale calls never hit a reconfigured upstream.

### Universal Poll Manager & Socket-Hook Scope Detection

- Every configured server (whether built-in `Unreal MCP`, `Filesystem MCP`, or custom HTTP/STDIO) is monitored uniformly by `McpPollManager` (`Adapters/mcp-poll-manager.js`) and `McpServerStateCache` (`Adapters/mcp-state-cache.js`).
- Local servers (`scope: "local"`) are checked every `2500 ms`; remote servers (`scope: "remote"`) are checked every `30000 ms`.
- For custom STDIO servers (`npx`, `uvx`, `python`, `node`), Bridge injects lightweight socket hooks (`sitecustomize.py` for Python and `socket-hook.js` for Node.js) during startup/verification. If a STDIO wrapper connects to a local loopback port (such as a local 3D engine, database, or IDE socket), Bridge automatically classifies it as `local` and tracks the target port's availability without polling `tools/list` repeatedly.
- The `Filesystem MCP` gateway is independent so `ApplyAllowedRootsAsync` can update allowed folders and restart only port `8003` while `Dynamic Router` (`:8001`) and `Bridge MCP Gateway` (`:8004`) stay online.
- `Adapters/roots-filter.js` strips client-supplied `roots` capabilities during `initialize`, preventing remote clients from bypassing the local folder allow-list. If no folders are checked, `Filesystem MCP` is restricted to `Dependencies/EmptyFilesystemRoot`.

## Process ownership & RAM control

- `McpInfrastructureService` serializes start, stop, verification, and root-update operations.
- Disabling any STDIO server in the UI immediately terminates its `Supergateway` and child CLI processes to free RAM.
- Each managed process is assigned to a dedicated Windows Job Object (`WindowsJobObject.cs`) configured with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. If `MCPBridge.exe` exits or crashes, Windows automatically terminates all child processes.
- Normal shutdown performs a one-shot Toolhelp descendant sweep (`ProcessTreeTerminator.cs`) and terminates the Job Object, then verifies that all managed ports (`8001`, `8003`, `8004`, `8010–8089`) are released.
- Before starting the local pipeline, `StaleManagedProcessCleaner` inspects the WMI process snapshot and Windows TCP owner-PID table (`TcpPortInspector.cs`) to clean up only orphaned processes matching known Bridge command-line fingerprints. Unrelated `node.exe` or `supergateway.exe` processes are never touched.

## Dependency resolution

- The release installer (`MCPBridge-Setup-<version>.exe`) bundles `Filesystem MCP` (with its production `node_modules` tree), standalone `Supergateway`, `uv.exe` / `uvx.exe`, the `Tailscale` Windows x64 MSI, and the `Node.js` v26.13.0 x64 MSI.
- `.NET Framework 4.8` is included in Windows 10 (1903+) and Windows 11 x64.
- `Node.js` MSI is executed during setup only if no compatible Node.js runtime is detected on the machine.

## Logging

Logs are disabled by default and can be toggled in real time from the **Logs** tab. When enabled, logs are written under `Logs/`:
- `Logs/connections.log` — startup and status transitions of `Bridge MCP Gateway (:8004)`, `Dynamic Router (:8001)`, MCP upstreams, and Tailscale Funnel.
- `Logs/app.log` — application events, preset switches, allowed folder updates, and UI settings changes.
- `Logs/verification.log` — 4-stage server verification pipeline logs (`PRE-FLIGHT`, `TRANSPORT`, `MCP WORKER`, `TOOLS`).
- `Logs/errors.log` — unexpected child process exits, exceptions, HRESULT/Win32 codes, and sanitized stderr tails (Bearer tokens and API keys are automatically redacted).

## Tailscale Funnel integration

- The application defaults to **Local Only**. All local MCP features work without a Tailscale account.
- **Connect** under *Online access* launches the guided Tailscale sign-in and Funnel approval flow via `TailscaleConnectionManager` and `scripts/TailscaleSetup.ps1`.
- Bridge applies `accept-dns=false` and `unattended=true` so it never alters Windows DNS or NRPT settings.
- Funnel runs as a foreground process inside a Windows Job Object publishing `http://127.0.0.1:8004/mcp` to `https://<device>.<tailnet>.ts.net/mcp`.
- If Tailscale was already installed on the PC before MCP Bridge, uninstalling MCP Bridge preserves the user's Tailscale installation and restores its original settings.

## Remote HTTP upstream trust

- Loopback HTTP endpoints (`127.0.0.1` / `localhost`) connect directly.
- Remote HTTP MCP upstreams must use `https://` (plain HTTP is rejected) and require an explicit per-server origin confirmation (`scheme + host + port`) in the server editor.
- Imported JSON configurations cannot bypass this trust prompt.
