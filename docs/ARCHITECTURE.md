# Architecture

## Application lifetime

- A single normal-user WPF process owns the monitor and every MCP child it starts.
- Optional per-user startup uses `HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run`.
- While the manager process exists, WinEvent monitoring is active.
- Closing the window hides it to the tray. The explicit tray Exit action stops the monitor and owned child processes.
- When Unreal Editor closes, only the Unreal MCP upstream transitions to the `Waiting` state; the local MCP pipeline (Dynamic Router, Bridge MCP Gateway, Filesystem MCP, and custom servers) continues running uninterrupted.

## Editor discovery

- `EditorMonitor` uses `SetWinEventHook(EVENT_OBJECT_CREATE, WINEVENT_OUTOFCONTEXT)` for newly created Unreal Editor windows.
- At manager startup, `Process.GetProcesses()` is called once to include editors that were opened earlier.
- No recurring process-table polling, process-start WMI subscription, separate watcher, or Unreal Python bridge is used.
- The event is an early process/window discovery signal, not proof that the Editor has completed startup. Headless commandlets are not monitored.
- The command line of the known PID is read separately to extract `.uproject`; only this PID is retried briefly if the command line is not yet populated.
- Once a live Editor session has a project path, the manager repeatedly sends a real MCP `initialize` request to `127.0.0.1:8000/mcp` every 2.5 seconds. A successful HTTP status alone is insufficient: the JSON or SSE response must contain the JSON-RPC response for request ID `1` with a `result` and no `error`. The local MCP pipeline is not started until that protocol response is received. This is the authoritative readiness condition for this application; Windows process/window events cannot prove that shader compilation or all project initialization has finished.
- If Unreal MCP is not ready yet, the state remains a non-error wait state. The retry continues while the Editor session is alive, and is cancelled when the last Editor closes or the Manager exits. After 90 seconds the UI suggests checking the MCP plugin, without stopping retries.

## Local MCP pipeline

| Component | Local endpoint | Purpose |
|---|---|---|
| Unreal MCP (provided by UE) | `127.0.0.1:8000/mcp` | Existing Unreal Editor MCP server |
| Filesystem MCP gateway | `127.0.0.1:8003/filesystem/mcp` | Filesystem server restricted by the manager-owned roots allow-list |
| Dynamic Router (internal) | `127.0.0.1:8001/ue/mcp` | Aggregates enabled upstream MCP servers, routes tools/list and tools/call, strips client roots and Origin |
| Bridge MCP Gateway (external) | `127.0.0.1:8004/ue/mcp` | Stable MCP contract exposed to clients; always publishes only `all_tools` and `call_tool` |

All listeners bind only to `127.0.0.1`. Tailscale Funnel publishes the Bridge MCP Gateway on port 8004 only after both the internal Router and external Gateway are ready. Funnel runs as a foreground CLI process (never `--bg`) inside a Windows Job Object. Normal Bridge shutdown removes the managed Funnel route and terminates the Funnel host process while leaving the Windows Tailscale service running.

The Bridge MCP Gateway is a separate managed Node process with its own lifecycle and Job Object. It forwards to the existing internal Router on 8001 and does not start, stop or reconfigure upstream MCP processes. The internal Router remains the owner of upstream transport sessions and dispatch. The Gateway exposes exactly two permanent tools: `all_tools` and `call_tool`. It never advertises upstream tools individually and does not announce external `tools/list_changed`.

The Gateway contains independent modules for Router transport, catalog management, catalog invalidation, schema validation and tool dispatch. The manager-owned loopback endpoint `POST /__manager/catalog/invalidate` updates the server status snapshot and marks the catalog dirty; it does not perform an upstream scan. Bridge calls it after upstream configuration changes or detected availability transitions. The next `all_tools` call performs one aggregate `tools/list` through the Router; concurrent calls share that refresh. Subsequent calls reuse the cached snapshot until another invalidation. There is no periodic `tools/list` catalog scan; the existing lightweight server health checks continue. A stale or unavailable tool call also marks the catalog dirty so the next `all_tools` refreshes it. An explicit user request can force a one-time scan; normal catalog calls remain cached. Changes made only inside an upstream that stays online are not discovered proactively unless that upstream triggers an invalidation; the user can request a one-time forced scan when needed. `catalog_revision` increments only when the actual catalog or server state changes. Ordinary `call_tool` calls do not rescan upstream servers. The Gateway does not automatically repeat a potentially side-effecting operation.

The Filesystem MCP gateway is independent so `ApplyAllowedRootsAsync` can stop and restart only its process. The Dynamic Router and Bridge MCP Gateway remain online. The roots-filter prevents a remote MCP client from replacing the root allow-list through its initialize capabilities.

If no roots are configured, the Filesystem MCP service is restricted to the empty manager-owned directory `Dependencies/EmptyFilesystemRoot`.

## Dependency resolution

- Node.js is installed system-wide through the official x64 MSI only when no compatible installation is found. Setup's discovery checks process/user/machine PATH, Node.js MSI registry registration, App Paths, Add/Remove Programs uninstall entries, standard install directories and common version-manager locations. It evaluates all candidates and selects the highest runnable version. For a previous official Node MSI installation, Setup reads `SOFTWARE\Node.js\InstallPath` and passes that directory as `INSTALLDIR` to preserve the existing installation location during the MSI major upgrade. The Node.js v26.13.0 MSI is read from the configured shared build-dependencies folder and SHA-256 verified; it is extracted and executed only when the detected runtime does not satisfy the bundled Filesystem MCP package's `engines.node` range.
- Filesystem MCP and standalone Supergateway are resolved from the installer's `Dependencies/` payload first; existing per-user/global installations are development fallbacks. Tailscale is installed from a pre-bundled Windows x64 MSI in the build dependency folder; Setup does not download it.
- The installer targets Windows 10 version 1903 x64 (build 18362) and newer, plus Windows 11 x64. The application targets .NET Framework 4.8, which is included in these Windows versions. No .NET bootstrapper is bundled or launched. Setup checks for .NET Framework 4.8 (Release >= 528040) as a system prerequisite and installs the Node.js MSI only when no compatible runtime is present. During installation it updates the status line for Node.js and final dependency verification.
- The release installer carries the complete Filesystem MCP production dependency tree, standalone Supergateway, Tailscale Windows x64 MSI and Node.js x64 MSI. .NET Framework 4.8 is supplied by the supported Windows operating systems and is not bundled separately. No runtime dependency download or package-manager invocation occurs.

## Process ownership and shutdown

- `McpInfrastructureService` serializes start, stop and root-update operations.
- Managed process stdout/stderr are captured into a bounded in-memory tail and only appended to `Logs/errors.log` when a process unexpectedly exits or startup fails.
- Each long-lived managed root process is assigned to its own Windows Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. This makes Windows terminate the job members when the manager process crashes or exits and closes its last job handle.
- Normal shutdown performs a one-shot Toolhelp descendant sweep while the root PID is still valid (covering the short interval before Job Object assignment), then calls `TerminateJobObject` and waits for the root. No process-tree polling is used.
- Before starting the local pipeline, a WMI process snapshot and the Windows TCP owner-PID table are used to identify stale processes from known Manager command lines. A process is terminated automatically only when it owns a reserved port, matches a known Manager executable/config/script fingerprint, and its parent is missing or its parent PID has demonstrably been reused. Unrelated `node.exe` and `supergateway.exe` processes are not terminated.
- The managed listener ports are 8001, 8003 and 8004 (plus 8010–8089 for custom STDIO servers); 8002 is not used by the active pipeline. After stopping, the manager verifies that its active ports have actually been released.
- Manager exit stops Bridge MCP Gateway, Dynamic Router, Filesystem gateway, and all managed custom STDIO servers.

## Logging

Logs are separated into three files under `Logs/`:
- `Logs/connections.log` — startup and status transitions of Bridge MCP Gateway (`:8004`), Dynamic Router (`:8001`), MCP upstreams, auto-attach events, and Tailscale Funnel.
- `Logs/app.log` — application events, preset changes, allowed root updates, and UI settings changes.
- `Logs/errors.log` — errors, unhandled exceptions, and unexpected exits of managed child processes (UTC timestamp, source, HRESULT, exception details, WMI/Win32 codes, and bounded stdout/stderr tail).

## Tailscale integration

### Installer flow

- The sole release pipeline is `scripts/Build-Installer.ps1` (the **Build Installer** action). It publishes the WPF application, prepares the local dependency payload, and compiles `installer/MCPBridge.iss`. The updated `scripts/TailscaleSetup.ps1` is included both as an installed helper and as the embedded resource used by the application.
- During Setup post-install, `installer/MCPBridge.iss` calls `EnsureTailscaleRuntime`, which runs `Scripts/TailscaleSetup.ps1 -Mode Install`. The helper checks the target machine before it considers running the bundled MSI.
- Discovery checks the Windows Tailscale service registration and its `ImagePath`, actual `tailscale.exe` / `tailscaled.exe` files, registered Tailscale `InstallLocation` values and both 64-bit and 32-bit uninstall registry views. The service path is not trusted without checking files in the corresponding directory.
- A complete existing installation (CLI plus registered service, with a valid daemon path when one is registered) is reused. Setup captures the original service startup type, running/stopped state and service ACL if no Bridge state file exists; it marks `InstalledByMcpBridge=0`. It does not execute `msiexec /i` and does not repair, upgrade or replace the user's Tailscale installation.
- If there is no evidence of an existing installation, Setup verifies the bundled MSI's Authenticode signature, saves a copy as `Dependencies/tailscale-installed.msi`, and runs the MSI silently with `TS_NOLAUNCH=1`. The state file records `InstalledByMcpBridge=1`, allowing a later full uninstall to remove only the MSI Bridge installed.
- If only partial or conflicting evidence remains (for example, a registered service without its CLI, an MSI registration without a working service/CLI pair, or Tailscale binaries without the service), Setup stops with a diagnostic. It deliberately does not layer a second MSI over a damaged or half-removed installation.
- The old uninstaller receives the `UpgradeInProgress` marker during an in-place Bridge upgrade. Its Tailscale cleanup helper preserves the installed Tailscale package and its state rather than uninstalling it as part of a Bridge upgrade.

### Runtime flow

- The application defaults to **Local Only**. Unreal MCP, Filesystem MCP, Dynamic Router and Bridge MCP Gateway work locally without a Tailscale account or public endpoint.
- The **Set up online access** action starts the existing Tailscale service when necessary, then launches the Tailscale GUI and sign-in flow. If Windows denies service start, Bridge invokes the elevated `PrepareOnline` helper; it does not rewrite the service startup type or ACL.
- After the user signs in and the device receives a Tailscale IPv4 address, Bridge runs the `Activate` helper. It saves the original `accept-dns` and `unattended` preferences once, applies and verifies `accept-dns=false` and `unattended=true`, then verifies the device state and Funnel capability. These operations do not reinstall Tailscale.
- Account sign-in and Funnel enablement are separate browser approvals. The first Tailscale login link uses `/a/...`. If the tailnet requires Funnel enablement, the CLI/PowerShell helper returns a second link at `https://login.tailscale.com/f/funnel?node=...`; Bridge validates and opens that exact approval page, then continues probing until the Funnel configuration is confirmed. The running Funnel host also watches its own CLI output for the same second approval URL.
- A separate foreground Funnel process publishes `/ue/mcp` to local Bridge MCP Gateway port 8004 only after both the internal Router and external Gateway are ready. The local MCP infrastructure does not depend on Tailscale.
- Disabling online access removes the Bridge-managed Funnel route and terminates its host process. The Tailscale Windows service remains running; normal Bridge shutdown also stops the Funnel host without uninstalling or deleting the Tailscale client.
- MCP Bridge never changes Windows DNS servers, adapter DNS or NRPT policy.
- Funnel requires MagicDNS, HTTPS certificates and the `funnel` node attribute in tailnet policy. A connected device may still need a separate one-time Funnel approval.
- On full uninstall, Bridge removes Tailscale only when `InstalledByMcpBridge=1`. If Tailscale pre-existed, Bridge restores the saved service ACL, startup type, running/stopped state and any original Tailscale preferences saved during first activation.
- The public URL is runtime-only and is cleared when Funnel stops. The public endpoint is reachable to anyone who knows the URL; the Filesystem MCP allow-list remains the file-access security boundary.

## Storage and installation

All application-owned mutable data remains under the install directory: `Config/`, `Dependencies/`, `Adapters/`, `Logs/`. The per-user Windows Run registry key is used only for the optional MCP Bridge startup preference; MCP servers and Tailscale are not independently registered for autostart. Tailscale authorization state is owned by the Tailscale client and is not copied into `settings.json`. The final installer bundles Filesystem MCP (including its production npm dependency tree), standalone Supergateway, the locally supplied Tailscale MSI and the Node.js v26.13.0 x64 MSI. .NET Framework 4.8 is provided by Windows 10 1903+ and Windows 11. The Node.js MSI is used only if the target has no compatible Node runtime. Write access is granted only on the mutable application subdirectories.

## Remote HTTP upstream trust

- User-configured HTTP MCP upstreams may use `http://` only when the destination is a loopback address. Remote upstreams must use `https://`; remote plain HTTP is rejected.
- Adding or changing a remote endpoint requires an explicit confirmation in the server editor. The approval is scoped to the individual server entry and its normalized HTTPS origin (scheme, host and port), not to arbitrary hosts or all servers.
- The trust value is stored in that server entry in `Config/settings.json`. It is not accepted from imported MCP JSON or the free-form additional JSON field. A changed origin requires a new confirmation; the runtime independently checks the stored approval before probing or publishing an upstream to the Dynamic Router.
- The confirmation warns that MCP requests and every configured HTTP header, including bearer credentials, are sent to the approved host. Header values are also stored in `Config/settings.json`; protect that file accordingly. TLS certificate validation remains enabled, HTTP redirects are not followed, and a legacy SSE `endpoint` event must resolve to the same origin as the configured MCP URL.
- The internal Dynamic Router endpoint and every Bridge listener remain loopback-only. Remote upstream support does not change the listener bind addresses or expose a new inbound port.

