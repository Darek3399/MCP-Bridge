using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using McpBridge.Models;

namespace McpBridge.Services;

public sealed class McpVerificationStepReport
{
    public string Step1Preflight { get; set; } = I18n.Tr("Ожидание...", "Waiting...");
    public string Step2Transport { get; set; } = I18n.Tr("Ожидание...", "Waiting...");
    public string Step3Initialize { get; set; } = I18n.Tr("Ожидание...", "Waiting...");
    public string Step4Tools { get; set; } = I18n.Tr("Ожидание...", "Waiting...");
    public bool IsDone { get; set; }
    public bool IsSuccess { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? DiscoveredServerName { get; set; }
    public int ToolsCount { get; set; }
    public List<string> ToolNames { get; set; } = new();
    public string DiagnosticOutput { get; set; } = string.Empty;
}

public sealed class McpInfrastructureStatusEventArgs : EventArgs
{
    public McpInfrastructureStatusEventArgs(
        string unrealStatus, string filesystemStatus, string gatewayStatus,
        IReadOnlyList<McpServerStatusSnapshot> servers, IReadOnlyList<McpServerStatusSnapshot> enabledServers, int onlineCount, int enabledCount, int totalToolsCount)
    {
        UnrealStatus = unrealStatus; FilesystemStatus = filesystemStatus; GatewayStatus = gatewayStatus;
        Servers = servers; EnabledServers = enabledServers; OnlineCount = onlineCount; EnabledCount = enabledCount; TotalToolsCount = totalToolsCount;
    }
    public string UnrealStatus { get; }
    public string FilesystemStatus { get; }
    public string GatewayStatus { get; }
    public IReadOnlyList<McpServerStatusSnapshot> Servers { get; }
    public IReadOnlyList<McpServerStatusSnapshot> EnabledServers { get; }
    public int OnlineCount { get; }
    public int EnabledCount { get; }
    public int TotalToolsCount { get; }
}

public sealed class McpInfrastructureService : IDisposable
{
    private enum ConfigurationTransition
    {
        Initial,
        Unchanged,
        Changed
    }

    private const string LoopbackPrefix = "http" + "://127.0.0.1:";
    private static readonly string UnrealEndpoint = LoopbackPrefix + "8000/mcp";
    private static readonly string FilesystemEndpoint = LoopbackPrefix + "8003/filesystem/mcp";
    private static readonly string FilteredEndpoint = LoopbackPrefix + "8001/mcp";
    private static readonly string BridgeEndpoint = LoopbackPrefix + "8004/mcp";
    private static readonly string RouterHealthEndpoint = LoopbackPrefix + "8001/__manager/health";
    private static readonly string RouterUpstreamsEndpoint = LoopbackPrefix + "8001/__manager/upstreams";
    private static readonly string BridgeHealthEndpoint = LoopbackPrefix + "8004/__manager/health";
    private const string ProtocolVersion = "2025-06-18";
    private const int FilesystemPort = 8003;
    private const int FilterPort = 8001;
    private const int BridgeMcpPort = 8004;
    private const int CustomPortRangeStart = 8010;
    private const int CustomPortRangeEnd = 8089;
    private const int VerifyPortRangeStart = 8090;
    private const int VerifyPortRangeEnd = 8099;
    private const int GatewayStartupTimeoutMilliseconds = 25000;

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _routerPushGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly string _managerApiKey = CreateManagerApiKey();
    private readonly McpPollManagerClient _pollManagerClient;
    private CancellationTokenSource? _pollCacheCancellation;
    private Task? _pollCacheSubscriptionTask;
    private System.Threading.Timer? _managedProcessMonitorTimer;
    private readonly Dictionary<string, ManagedCustomStdioRuntime> _customStdioRuntimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServerRuntimeStatus> _statusById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _desiredEnabledById = new(StringComparer.OrdinalIgnoreCase);
    private volatile ManagedChild? _filesystemGateway;
    private volatile ManagedChild? _rootsFilter;
    private volatile ManagedChild? _bridgeGateway;
    private DateTime _nextBridgeGatewayRetryUtc = DateTime.MinValue;
    private DateTime _nextRouterRetryUtc = DateTime.MinValue;
    private string? _lastPushedUpstreamsFingerprint;
    // Last configurations acknowledged by the Router, tracked uniformly by MCP server ID.
    // Entries survive child-process restarts but are reset with this manager session.
    private readonly Dictionary<string, string> _lastPublishedConfigurationFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private string _lastRouterProxyFingerprint = string.Empty;
    private volatile bool _disposed;
    private volatile bool _stopping;
    private bool _stopCompleted;
    private volatile bool _routerReady;
    private volatile bool _lastPublishedGatewayReady;
    private string _lastPublishedUiFingerprint = string.Empty;
    private string _unrealStatus = "Выключен";
    private string _filesystemStatus = "Не запущен";
    private string _gatewayStatus = "Не запущен";
    private string[] _activeRoots = Array.Empty<string>();
    private string[] _desiredRoots = Array.Empty<string>();
    private string _activeFilesystemFingerprint = string.Empty;
    private bool _unrealEnabled = false; private bool _unrealDeleted; private bool _filesystemDeleted; private CustomMcpServerEntry? _unrealCustom; private CustomMcpServerEntry? _filesystemCustom;
    private bool _filesystemEnabled = true;
    private List<CustomMcpServerEntry> _customServers = new();

    public McpInfrastructureService()
    {
        _pollManagerClient = new McpPollManagerClient(_managerApiKey);
    }

    public event EventHandler<McpInfrastructureStatusEventArgs>? StatusChanged;

    private bool IsRouterReady => _routerReady && _rootsFilter != null && !IsExited(_rootsFilter.Process);
    private bool IsBridgeGatewayReady => _bridgeGateway != null && !IsExited(_bridgeGateway.Process);
    public bool IsReady => IsRouterReady && IsBridgeGatewayReady;
    public bool CanApplyAllowedRoots => IsRouterReady;
    public string LocalEndpoint => BridgeEndpoint;

    public async Task<bool> IsUnrealMcpReadyAsync(int timeoutMilliseconds = 1500)
    {
        await RefreshPollCacheSnapshotAsync().ConfigureAwait(false);
        return _statusById.TryGetValue(SettingsStore.BuiltInUnrealId, out var status) &&
            status.HealthState == McpServerHealthState.Online;
    }

    private bool IsDesiredEnabled(string serverId, bool fallback = true)
    {
        lock (_stateLock)
            return _desiredEnabledById.TryGetValue(serverId, out var enabled) ? enabled : fallback;
    }

    public void SetServerEnabledImmediate(string serverId, bool isEnabled)
    {
        if (_disposed || _stopping || string.IsNullOrWhiteSpace(serverId)) return;
        lock (_stateLock)
        {
            _desiredEnabledById[serverId] = isEnabled;
            if (string.Equals(serverId, SettingsStore.BuiltInUnrealId, StringComparison.OrdinalIgnoreCase))
                _unrealEnabled = isEnabled;
            else if (string.Equals(serverId, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase))
                _filesystemEnabled = isEnabled;
            else
            {
                var custom = _customServers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));
                if (custom != null) custom.Enabled = isEnabled;
            }

            if (_statusById.TryGetValue(serverId, out var status))
                ApplyDesiredEnabledToStatus(status, isEnabled);
        }
        PublishCurrentSnapshot();
    }

    private static void ApplyDesiredEnabledToStatus(ServerRuntimeStatus status, bool isEnabled)
    {
        status.IsEnabled = isEnabled;
        if (!isEnabled)
        {
            var isStdio = string.Equals(status.TransportBadge, "STDIO -> HTTP", StringComparison.OrdinalIgnoreCase);
            if (isStdio && !string.Equals(status.Id, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase))
                status.EndpointDisplay = "-";
            status.HealthState = McpServerHealthState.Disabled;
            status.ToolsCount = 0;
            status.ToolsPreview = string.Empty;
            status.StatusText = isStdio
                ? I18n.Tr("Выключен (процесс остановлен)", "Disabled (process stopped)")
                : I18n.Tr("Выключен", "Disabled");
        }
        else if (status.HealthState == McpServerHealthState.Disabled)
        {
            var isStdio = string.Equals(status.TransportBadge, "STDIO -> HTTP", StringComparison.OrdinalIgnoreCase);
            status.HealthState = (!isStdio || IsServerPackageReady(status.Id))
                ? McpServerHealthState.Waiting
                : McpServerHealthState.Downloading;
            status.StatusText = status.HealthState == McpServerHealthState.Downloading
                ? I18n.Tr("Скачивание и запуск пакета MCP…", "Downloading & starting MCP package…")
                : (isStdio
                    ? I18n.Tr("Установка связи MCP…", "Establishing MCP connection…")
                    : I18n.Tr("Подключение к MCP-серверу…", "Connecting to MCP server…"));
        }
    }

    public void PrepopulateServers(AppSettings settings, IEnumerable<string> enabledRoots)
    {
        if (_disposed || _stopping) return;
        _unrealDeleted = settings.UnrealMcpDeleted;
        _filesystemDeleted = settings.FilesystemMcpDeleted;
        _unrealCustom = settings.UnrealMcpCustom?.Clone();
        _filesystemCustom = settings.FilesystemMcpCustom?.Clone();
        _unrealEnabled = !_unrealDeleted && (settings.UnrealMcpEnabled ?? false);
        _filesystemEnabled = !_filesystemDeleted && (settings.FilesystemMcpEnabled ?? true);
        _customServers = (settings.CustomServers ?? new List<CustomMcpServerEntry>()).Select(s => s.Clone()).ToList();
        _desiredRoots = NormalizeRoots(enabledRoots);

        _desiredEnabledById[SettingsStore.BuiltInUnrealId] = _unrealEnabled;
        _desiredEnabledById[SettingsStore.BuiltInFilesystemId] = _filesystemEnabled;
        foreach (var s in _customServers)
            _desiredEnabledById[s.Id] = s.Enabled;

        if (!_unrealDeleted)
        {
            if (!_statusById.TryGetValue(SettingsStore.BuiltInUnrealId, out var uStatus))
            {
                var uUrl = (_unrealCustom != null && !string.IsNullOrWhiteSpace(_unrealCustom.Url)) ? _unrealCustom.Url! : UnrealEndpoint;
                var uName = (_unrealCustom != null && !string.IsNullOrWhiteSpace(_unrealCustom.Name)) ? _unrealCustom.Name! : "Unreal MCP";
                var uPort = 8000;
                var uPath = "/mcp";
                if (Uri.TryCreate(uUrl, UriKind.Absolute, out var pUri)) { uPort = pUri.Port; uPath = pUri.AbsolutePath; }
                _statusById[SettingsStore.BuiltInUnrealId] = new ServerRuntimeStatus
                {
                    Id = SettingsStore.BuiltInUnrealId,
                    Name = uName,
                    Subtitle = I18n.Tr("Автоподключение к Unreal Editor (:", "Auto-attach to Unreal Editor (:") + uPort + ")",
                    EndpointDisplay = uPort.ToString(),
                    PathDisplay = uPath,
                    TransportBadge = "HTTP / AUTO",
                    UpstreamUrl = uUrl,
                    IsBuiltIn = true,
                    IsEnabled = _unrealEnabled,
                    HealthState = _unrealEnabled ? McpServerHealthState.Waiting : McpServerHealthState.Disabled,
                    StatusText = _unrealEnabled ? I18n.Tr("Подключение к MCP-серверу…", "Connecting to MCP server…") : I18n.Tr("Выключен", "Disabled")
                };
            }
            else
            {
                ApplyDesiredEnabledToStatus(uStatus, _unrealEnabled);
            }
        }

        if (!_filesystemDeleted)
        {
            if (!_statusById.TryGetValue(SettingsStore.BuiltInFilesystemId, out var fsStatus))
            {
                var fsName = (_filesystemCustom != null && !string.IsNullOrWhiteSpace(_filesystemCustom.Name)) ? _filesystemCustom.Name! : "Filesystem MCP";
                _statusById[SettingsStore.BuiltInFilesystemId] = new ServerRuntimeStatus
                {
                    Id = SettingsStore.BuiltInFilesystemId,
                    Name = fsName,
                    Subtitle = I18n.Tr("Доступ к отмеченным галочками папкам проектов", "Access to checked project folders"),
                    EndpointDisplay = "8003",
                    PathDisplay = "/filesystem/mcp",
                    TransportBadge = "STDIO -> HTTP",
                    UpstreamUrl = FilesystemEndpoint,
                    IsBuiltIn = true,
                    IsEnabled = _filesystemEnabled,
                    HealthState = _filesystemEnabled ? McpServerHealthState.Waiting : McpServerHealthState.Disabled,
                    StatusText = _filesystemEnabled ? I18n.Tr("Установка связи MCP…", "Establishing MCP connection…") : I18n.Tr("Выключен (процесс остановлен)", "Disabled (process stopped)")
                };
            }
            else
            {
                ApplyDesiredEnabledToStatus(fsStatus, _filesystemEnabled);
            }
        }

        foreach (var server in _customServers)
        {
            if (_statusById.TryGetValue(server.Id, out var existingStatus))
            {
                ApplyDesiredEnabledToStatus(existingStatus, server.Enabled);
                continue;
            }
            var isHttp = string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase);
            var endpointDisp = "-";
            var pathDisp = "/" + server.Id + "/mcp";
            if (isHttp)
            {
                if (Uri.TryCreate(server.Url, UriKind.Absolute, out var uri))
                {
                    endpointDisp = uri.Port.ToString();
                    pathDisp = uri.AbsolutePath;
                }
                else
                {
                    endpointDisp = "HTTP";
                    pathDisp = server.Url ?? string.Empty;
                }
            }
            var initialHealth = !server.Enabled
                ? McpServerHealthState.Disabled
                : (isHttp || IsServerPackageReady(server.Id)
                    ? McpServerHealthState.Waiting
                    : McpServerHealthState.Downloading);
            var initialText = !server.Enabled
                ? (isHttp ? I18n.Tr("Выключен", "Disabled") : I18n.Tr("Выключен (процесс остановлен)", "Disabled (process stopped)"))
                : (initialHealth == McpServerHealthState.Downloading
                    ? I18n.Tr("Скачивание и запуск пакета MCP…", "Downloading & starting MCP package…")
                    : (isHttp
                        ? I18n.Tr("Подключение к MCP-серверу…", "Connecting to MCP server…")
                        : I18n.Tr("Установка связи MCP…", "Establishing MCP connection…")));

            _statusById[server.Id] = new ServerRuntimeStatus
            {
                Id = server.Id,
                Name = server.Name,
                Subtitle = isHttp
                    ? I18n.Tr("Внешний MCP · ", "External MCP · ") + (server.Url ?? string.Empty)
                    : (server.Command ?? string.Empty) + " " + McpServerConfigParser.FormatArguments(server.Args),
                EndpointDisplay = endpointDisp,
                PathDisplay = pathDisp,
                TransportBadge = isHttp ? "HTTP / AUTO" : "STDIO -> HTTP",
                UpstreamUrl = isHttp ? (server.Url ?? string.Empty) : string.Empty,
                IsBuiltIn = false,
                IsEnabled = server.Enabled,
                HealthState = initialHealth,
                StatusText = initialText
            };
        }

        PublishCurrentSnapshot();
    }

    private static bool IsServerPackageReady(string? serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId) ||
            string.Equals(serverId, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(serverId, SettingsStore.BuiltInUnrealId, StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            var dir = AppPaths.GetServerPackageDirectory(serverId!);
            if (!Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, ".ready"))) return true;

            foreach (var sub in new[] { "ut", "n", "np", "uc", "uv-tools", "npm-cache", "npm-prefix", "uv-cache" })
            {
                var subPath = Path.Combine(dir, sub);
                if (Directory.Exists(subPath) && Directory.EnumerateFileSystemEntries(subPath).Any())
                {
                    MarkServerPackageReady(serverId);
                    return true;
                }
            }
            return false;
        }
        catch { return false; }
    }

    private static void MarkServerPackageReady(string? serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId) ||
            string.Equals(serverId, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(serverId, SettingsStore.BuiltInUnrealId, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            var dir = AppPaths.GetServerPackageDirectory(serverId!);
            Directory.CreateDirectory(dir);
            var marker = Path.Combine(dir, ".ready");
            if (!File.Exists(marker))
                File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        }
        catch { }
    }

    private static void DeleteServerArtifacts(string? serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId) ||
            string.Equals(serverId, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(serverId, SettingsStore.BuiltInUnrealId, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var cfg = AppPaths.GetCustomGatewayConfigPath(serverId!);
            if (File.Exists(cfg)) File.Delete(cfg);
        }
        catch { }

        try
        {
            var safeId = SettingsStore.SanitizeServerId(serverId!);
            if (!string.IsNullOrWhiteSpace(safeId))
            {
                var netLog = Path.Combine(AppPaths.CustomGatewaysDirectory, "net-" + safeId + ".jsonl");
                if (File.Exists(netLog)) File.Delete(netLog);
            }
        }
        catch { }

        var pkgDir = AppPaths.GetServerPackageDirectory(serverId!);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (DeleteDirectoryForce(pkgDir))
                break;
            Thread.Sleep(150);
        }
    }

    private static bool DeleteDirectoryForce(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        try
        {
            if (!Directory.Exists(path)) return true;
            Directory.Delete(path, true);
            return !Directory.Exists(path);
        }
        catch
        {
            try
            {
                var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var extended = fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
                    ? fullPath
                    : (fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                        ? @"\\?\UNC\" + fullPath.Substring(2)
                        : @"\\?\" + fullPath);
                DeleteExtendedDirectoryRecursive(extended);
                return !Directory.Exists(path);
            }
            catch
            {
                return !Directory.Exists(path);
            }
        }
    }

    private static void DeleteExtendedDirectoryRecursive(string extendedDir)
    {
        var findHandle = FindFirstFileW(extendedDir + @"\*", out var findData);
        if (findHandle != new IntPtr(-1))
        {
            try
            {
                do
                {
                    var entryName = findData.cFileName;
                    if (string.IsNullOrEmpty(entryName) || entryName == "." || entryName == "..")
                        continue;

                    var childExtended = extendedDir + @"\" + entryName;
                    var isDirectory = (findData.dwFileAttributes & 0x10u) != 0;
                    var isReparsePoint = (findData.dwFileAttributes & 0x400u) != 0;
                    var isReadOnly = (findData.dwFileAttributes & 0x01u) != 0;

                    if (isReadOnly)
                        SetFileAttributesW(childExtended, 0x80u);

                    if (isDirectory && !isReparsePoint)
                    {
                        DeleteExtendedDirectoryRecursive(childExtended);
                    }
                    else if (isDirectory)
                    {
                        RemoveDirectoryW(childExtended);
                    }
                    else
                    {
                        DeleteFileW(childExtended);
                    }
                } while (FindNextFileW(findHandle, out findData));
            }
            finally
            {
                FindClose(findHandle);
            }
        }

        SetFileAttributesW(extendedDir, 0x80u);
        RemoveDirectoryW(extendedDir);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? cFileName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 14)]
        public string? cAlternateFileName;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetFileAttributesW(string lpFileName, uint dwFileAttributes);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string lpFileName);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool RemoveDirectoryW(string lpPathName);

    public void CleanupOrphanedServerPackages(AppSettings? settings = null)
    {
        try
        {
            var activeList = settings?.CustomServers ?? _customServers;
            var activeIds = new HashSet<string>(
                activeList.Select(s => SettingsStore.SanitizeServerId(s.Id)).Where(s => !string.IsNullOrWhiteSpace(s)),
                StringComparer.OrdinalIgnoreCase)
            {
                SettingsStore.BuiltInFilesystemId,
                SettingsStore.BuiltInUnrealId,
                "_shared"
            };

            if (Directory.Exists(AppPaths.ServerPackagesDirectory))
            {
                foreach (var dir in Directory.GetDirectories(AppPaths.ServerPackagesDirectory))
                {
                    var name = Path.GetFileName(dir);
                    if (!activeIds.Contains(name))
                    {
                        DeleteDirectoryForce(dir);
                    }
                }
            }

            if (Directory.Exists(AppPaths.CustomGatewaysDirectory))
            {
                foreach (var file in Directory.GetFiles(AppPaths.CustomGatewaysDirectory, "*.json"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var id = name.StartsWith("gateway-", StringComparison.OrdinalIgnoreCase)
                        ? name.Substring("gateway-".Length)
                        : name.StartsWith("verify-", StringComparison.OrdinalIgnoreCase)
                            ? name.Substring("verify-".Length)
                            : name;
                    if (!activeIds.Contains(SettingsStore.SanitizeServerId(id)) &&
                        !_customStdioRuntimes.Values.Any(r => r.EndpointUrl.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
        }
        catch { }
    }

    public void ReportWaitingForUnrealMcp() => PublishCurrentSnapshot();

    public void RefreshLocalizedStatusSnapshot()
    {
        if (_disposed || _stopping) return;
        PublishCurrentSnapshot();
    }

    public async Task SyncStateAsync(AppSettings settings, IEnumerable<string> enabledRoots, bool forceRetryQuarantined = false, string? forceRetryServerId = null)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            lock (_stateLock)
            {
                PrepopulateServers(settings, enabledRoots);
            }

            var dependencies = await Task.Run(() => McpDependencyResolver.Resolve()).ConfigureAwait(false);
            var routerNeedsStart = !IsRouterReady;
            if (routerNeedsStart)
            {
                await StopRouterForRestartAsync().ConfigureAwait(false);
                StaleManagedProcessCleaner.CleanupKnownOrphans();
            }

            // 1. Мгновенно настраиваем все HTTP-серверы (не требуют запуска локальных процессов)
            if (_unrealCustom != null && !string.Equals(_unrealCustom.Transport, "http", StringComparison.OrdinalIgnoreCase))
            {
                _unrealCustom.Id = SettingsStore.BuiltInUnrealId;
                _unrealCustom.Enabled = _unrealEnabled;
                await ReconcileCustomStdioServerAsync(dependencies, _unrealCustom, string.Equals(forceRetryServerId, SettingsStore.BuiltInUnrealId, StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
            }
            else
            {
                await StopCustomStdioServerAsync(SettingsStore.BuiltInUnrealId).ConfigureAwait(false);
                await ReconcileUnrealServerAsync().ConfigureAwait(false);
            }
            foreach (var httpServer in _customServers.Where(s => string.Equals(s.Transport, "http", StringComparison.OrdinalIgnoreCase)))
            {
                await StopCustomStdioServerAsync(httpServer.Id).ConfigureAwait(false);
                await ReconcileCustomHttpServerAsync(httpServer).ConfigureAwait(false);
            }
            PublishCurrentSnapshot();

            // 2. Первым поднимаем встроенный Filesystem MCP (:8003), чтобы при старте роутера он стал ONLINE за 0 мс
            await ReconcileFilesystemServerAsync(
                dependencies,
                _desiredRoots,
                (forceRetryQuarantined &&
                 _statusById.TryGetValue(SettingsStore.BuiltInFilesystemId, out var filesystemStatus) &&
                 filesystemStatus.HealthState == McpServerHealthState.Quarantine) ||
                string.Equals(forceRetryServerId, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
            PublishCurrentSnapshot();

            // 3. Запускаем роутер (:8001) и открываем SSE-канал статусов
            await PushUpstreamsToRouterAsync().ConfigureAwait(false);
            if (routerNeedsStart && !dependencies.HasNode)
            {
                _gatewayStatus = BuildNodeDependencyUnavailableMessage(dependencies);
                PublishCurrentSnapshot();
                EnsureManagedProcessMonitorStarted();
                return;
            }

            if (routerNeedsStart)
            {
                await StartRootsFilterAsync(dependencies).ConfigureAwait(false);
                _routerReady = true;
                _nextRouterRetryUtc = DateTime.MinValue;
                StartPollCacheSubscription();
                ErrorLog.LogConnection(I18n.Tr(
                    "Роутер шлюза (:8001) запущен и готов принимать подключения.",
                    "Gateway router (:8001) started and ready to accept connections."));
                await PushUpstreamsToRouterAsync().ConfigureAwait(false);
            }

            // 4. Запускаем внешний шлюз (:8004) и пользовательские STDIO-серверы параллельно
            var gatewayTask = EnsureBridgeMcpGatewayAsync(dependencies);
            var customStdioTask = ReconcileCustomServersAsync(dependencies, forceRetryQuarantined, forceRetryServerId);

            await Task.WhenAll(gatewayTask, customStdioTask).ConfigureAwait(false);
            await PushUpstreamsToRouterAsync().ConfigureAwait(false);
            PublishCurrentSnapshot();
            if (IsRouterReady)
            {
                _ = Task.Run(() => RefreshPollCacheSnapshotAsync());
            }
            EnsureManagedProcessMonitorStarted();
        }
        catch (Exception exception)
        {
            ErrorLog.Write("McpInfrastructureService.SyncStateAsync", exception);
            _gatewayStatus = I18n.Tr("Ошибка: ", "Error: ") + exception.Message;
            PublishCurrentSnapshot();
            EnsureManagedProcessMonitorStarted();
            throw;
        }
        finally { _operationGate.Release(); }
    }

    // Supervises local process lifetimes only. MCP protocol health belongs exclusively to Poll Manager Workers.
    private void EnsureManagedProcessMonitorStarted()
    {
        if (_disposed || _stopping || _managedProcessMonitorTimer != null) return;
        _managedProcessMonitorTimer = new System.Threading.Timer(
            state =>
            {
                _ = PollManagedProcessesSafelyAsync();
            },
            null,
            TimeSpan.FromMilliseconds(2500),
            TimeSpan.FromMilliseconds(2500));
    }

    private async Task PollManagedProcessesSafelyAsync()
    {
        try
        {
            await PollManagedProcessesAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ErrorLog.Write("McpInfrastructureService.ManagedProcessMonitor", exception);
        }
    }

    // This method never sends MCP initialize, discovery, tools/list or tools/call requests.
    public async Task PollManagedProcessesAsync()
    {
        if (_disposed || _stopping)
        {
            return;
        }
        if (!await _operationGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }
        var changed = false;
        try
        {
            if (_disposed || _stopping)
            {
                return;
            }
            if (!IsRouterReady)
            {
                if (DateTime.UtcNow < _nextRouterRetryUtc) return;

                if (_lastPublishedGatewayReady != IsReady)
                    PublishCurrentSnapshot();

                _routerReady = false;
                var routerDependencies = McpDependencyResolver.Resolve();
                if (!routerDependencies.HasNode)
                {
                    var dependencyMessage = BuildNodeDependencyUnavailableMessage(routerDependencies);
                    if (!string.Equals(_gatewayStatus, dependencyMessage, StringComparison.Ordinal))
                    {
                        _gatewayStatus = dependencyMessage;
                        PublishCurrentSnapshot();
                    }
                    _nextRouterRetryUtc = DateTime.UtcNow.AddSeconds(15);
                    return;
                }

                await StopRouterForRestartAsync().ConfigureAwait(false);
                StaleManagedProcessCleaner.CleanupKnownOrphans();

                // Reconcile process runtimes and persist the latest upstream
                // configuration before a replacement Router can start Workers.
                await ReconcileFilesystemServerAsync(
                    routerDependencies,
                    _desiredRoots,
                    forceRestartIfChanged: false).ConfigureAwait(false);
                await ReconcileUnrealServerAsync().ConfigureAwait(false);
                await ReconcileCustomServersAsync(
                    routerDependencies,
                    forceRetryQuarantined: false,
                    forceRetryServerId: null).ConfigureAwait(false);
                await PushUpstreamsToRouterAsync().ConfigureAwait(false);

                await StartRootsFilterAsync(routerDependencies).ConfigureAwait(false);
                _routerReady = true;
                _nextRouterRetryUtc = DateTime.MinValue;
                _lastPushedUpstreamsFingerprint = null;
                StartPollCacheSubscription();
                await PushUpstreamsToRouterAsync().ConfigureAwait(false);
                await EnsureBridgeMcpGatewayAsync(routerDependencies).ConfigureAwait(false);
                PublishCurrentSnapshot();
                return;
            }

            var dependencies = McpDependencyResolver.Resolve();
            var currentEnv = BuildRouterEnvironment(dependencies);
            var currentProxyFp = GetRouterProxyFingerprint(currentEnv);
            if (!string.Equals(_lastRouterProxyFingerprint, currentProxyFp, StringComparison.Ordinal))
            {
                _lastRouterProxyFingerprint = currentProxyFp;
                _lastPushedUpstreamsFingerprint = null;
                ErrorLog.LogConnection(I18n.Tr(
                    "Обнаружено изменение системных сетевых настроек/прокси Windows. Обновление параметров роутера (:8001)…",
                    "Windows system network/proxy settings changed. Updating router (:8001) proxy settings…"));
            }
            if (!IsBridgeGatewayReady && DateTime.UtcNow >= _nextBridgeGatewayRetryUtc)
                await EnsureBridgeMcpGatewayAsync(dependencies).ConfigureAwait(false);

            if (_filesystemEnabled && _filesystemGateway != null && IsExited(_filesystemGateway.Process))
            {
                await ReconcileFilesystemServerAsync(dependencies, _activeRoots, true).ConfigureAwait(false);
                changed = true;
            }

            foreach (var custom in _customServers.Where(server =>
                !string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase) && server.Enabled))
            {
                if (!_statusById.TryGetValue(custom.Id, out var status)) continue;
                var dependentPort = status.DependentLocalPort > 0
                    ? status.DependentLocalPort
                    : GuessDependentLocalPort(custom, status.LastDiagnosticLog);
                var hasRuntime = _customStdioRuntimes.TryGetValue(custom.Id, out var runtime);
                var runtimeAlive = hasRuntime && !IsExited(runtime!.Child.Process);
                if (dependentPort <= 0)
                {
                    if (hasRuntime && !runtimeAlive)
                    {
                        await ReconcileCustomStdioServerAsync(dependencies, custom, true).ConfigureAwait(false);
                        changed = true;
                    }
                    continue;
                }
                status.DependentLocalPort = dependentPort;

                var dependencyPids = TcpPortInspector.GetListeningProcessIds(dependentPort);
                var dependencyAlive = dependencyPids.Count > 0;

                if (!dependencyAlive)
                {
                    status.RuntimeStartFailed = false;
                    if (hasRuntime)
                    {
                        await StopCustomStdioServerAsync(custom.Id).ConfigureAwait(false);
                        changed = true;
                    }
                    if (status.HealthState != McpServerHealthState.Offline)
                    {
                        status.HealthState = McpServerHealthState.Offline;
                        status.ToolsCount = 0;
                        status.ToolsPreview = string.Empty;
                        status.StatusText = I18n.Tr("Сервер офлайн", "Server offline");
                        changed = true;
                    }
                }
                else if (!runtimeAlive)
                {
                    await ReconcileCustomStdioServerAsync(dependencies, custom, hasRuntime).ConfigureAwait(false);
                    changed = true;
                }
            }

            await PushUpstreamsToRouterAsync().ConfigureAwait(false);

            if (changed || _lastPublishedGatewayReady != IsReady)
                PublishCurrentSnapshot();
        }
        catch (Exception exception)
        {
            if (!IsRouterReady)
            {
                _routerReady = false;
                _nextRouterRetryUtc = DateTime.UtcNow.AddSeconds(15);
                _gatewayStatus = I18n.Tr(
                    "Роутер MCP Bridge недоступен; выполняется повторный запуск.",
                    "MCP Bridge router is unavailable; restart will be retried.");
                PublishCurrentSnapshot();
            }
            ErrorLog.Write("McpInfrastructureService.PollManagedProcessesAsync", exception);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static string SanitizeChildOutput(string? message, IDictionary<string, string>? environment)
    {
        var safe = message ?? string.Empty;
        safe = Regex.Replace(safe, @"(?i)\bBearer\s+[^\s,;]+", "Bearer [REDACTED]");
        safe = Regex.Replace(safe, @"(?i)\bgithub_pat_[A-Za-z0-9_]+\b|\bgh[pousr]_[A-Za-z0-9_]+\b", "[REDACTED]");
        safe = Regex.Replace(safe, @"(?i)(https?://)[^/@\s]+@", "$1[REDACTED]@");
        if (environment != null)
        {
            foreach (var item in environment)
            {
                if (string.IsNullOrEmpty(item.Value) || item.Value.Length < 4 ||
                    !Regex.IsMatch(item.Key, @"authorization|token|secret|api[-_]?key|credential|password|pat", RegexOptions.IgnoreCase))
                    continue;
                safe = safe.Replace(item.Value, "[REDACTED]");
            }
        }
        return safe;
    }

    private static string SanitizeUpstreamDiagnostic(string? message, IDictionary<string, string>? headers)
    {
        var safe = message ?? string.Empty;
        safe = Regex.Replace(safe, @"(?i)\bBearer\s+[^\s,;]+", "Bearer [REDACTED]");
        safe = Regex.Replace(safe, @"(?i)\bgithub_pat_[A-Za-z0-9_]+\b|\bgh[pousr]_[A-Za-z0-9_]+\b", "[REDACTED]");
        safe = Regex.Replace(safe, @"(?i)([?&](?:access_token|token|api[_-]?key|secret|authorization)=)[^&\s]+", "$1[REDACTED]");
        if (headers != null)
            foreach (var header in headers)
                if (!string.IsNullOrEmpty(header.Value) && Regex.IsMatch(header.Key, @"authorization|token|secret|api[-_]?key|credential", RegexOptions.IgnoreCase))
                    safe = safe.Replace(header.Value, "[REDACTED]");
        var filteredLines = safe.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.IndexOf("Supergateway is supported by", StringComparison.OrdinalIgnoreCase) < 0 &&
                        l.IndexOf("[supergateway] Starting...", StringComparison.OrdinalIgnoreCase) < 0 &&
                        l.IndexOf("- outputTransport:", StringComparison.OrdinalIgnoreCase) < 0 &&
                        l.IndexOf("- Headers:", StringComparison.OrdinalIgnoreCase) < 0)
            .ToArray();
        safe = string.Join(Environment.NewLine, filteredLines);
        return safe.Length > 600 ? "..." + safe.Substring(safe.Length - 597) : safe;
    }

    private static string DetermineServerScope(
        string transport,
        string? configuredUrl,
        string? command,
        IList<string>? args,
        IDictionary<string, string>? env,
        IDictionary<string, string>? headers,
        string? additionalJson,
        int dependentLocalPort = 0)
    {
        if (!string.IsNullOrWhiteSpace(additionalJson) &&
            McpServerConfigParser.DeserializeJson(additionalJson!) is IDictionary<string, object?> extra &&
            extra.TryGetValue("scope", out var rawScope) &&
            rawScope is string explicitScope)
        {
            if (string.Equals(explicitScope.Trim(), "local", StringComparison.OrdinalIgnoreCase)) return "local";
            if (string.Equals(explicitScope.Trim(), "remote", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(explicitScope.Trim(), "online", StringComparison.OrdinalIgnoreCase)) return "remote";
        }

        if (string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(configuredUrl) &&
                Uri.TryCreate(configuredUrl, UriKind.Absolute, out var httpUri))
                return httpUri.IsLoopback ? "local" : "remote";
            return "remote";
        }

        if (!string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            if (dependentLocalPort > 0) return "local";
        }

        var allArgs = args ?? Array.Empty<string>();
        foreach (var arg in allArgs)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            if (Regex.IsMatch(arg, @"(?:localhost|127\.0\.0\.1|\[::1\])\s*:\s*\d{2,5}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return "local";
            if (Uri.TryCreate(arg.Trim().Trim('"', '\''), UriKind.Absolute, out var argUri) &&
                (string.Equals(argUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(argUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                return argUri.IsLoopback ? "local" : "remote";
        }

        if (headers != null && headers.Count > 0) return "remote";

        if (env != null && env.Count > 0)
        {
            foreach (var pair in env)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
                if (Regex.IsMatch(pair.Value, @"(?:localhost|127\.0\.0\.1|\[::1\])\s*:\s*\d{2,5}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return "local";
                if (Uri.TryCreate(pair.Value.Trim().Trim('"', '\''), UriKind.Absolute, out var envUri) &&
                    (string.Equals(envUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(envUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
                    !envUri.IsLoopback)
                    return "remote";
                if (Regex.IsMatch(pair.Key, @"token|key|secret|auth|bearer|pat|password|credential|url|endpoint|host|cloud|api", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return "remote";
            }
        }

        var cmdLeaf = string.IsNullOrWhiteSpace(command)
            ? string.Empty
            : Path.GetFileName(command!.Trim().Trim('"')).ToLowerInvariant();
        if (cmdLeaf is "uvx" or "uvx.exe" or "npx" or "npx.cmd" or "pnpx" or "pnpx.cmd" or "bunx" or "bunx.exe" or "docker" or "docker.exe")
            return "remote";

        if (allArgs.Any(a => !string.IsNullOrWhiteSpace(a) &&
            (a.IndexOf("npx-cli.js", StringComparison.OrdinalIgnoreCase) >= 0 ||
             a.IndexOf("mcp-remote", StringComparison.OrdinalIgnoreCase) >= 0)))
            return "remote";

        foreach (var rawArg in allArgs)
        {
            if (string.IsNullOrWhiteSpace(rawArg)) continue;
            var candidatePath = Environment.ExpandEnvironmentVariables(rawArg.Trim().Trim('"'));
            if (candidatePath.StartsWith("-", StringComparison.Ordinal)) continue;
            try
            {
                if (Path.IsPathRooted(candidatePath) && (Directory.Exists(candidatePath) || File.Exists(candidatePath)))
                    return "local";
            }
            catch { }
        }

        return "remote";
    }

    private static bool CheckIfStdioLocalTargetOffline(string serverId, out bool isLocalScope)
    {
        isLocalScope = false;
        try
        {
            var safeKey = SettingsStore.SanitizeServerId(serverId);
            var netLogPath = Path.Combine(AppPaths.CustomGatewaysDirectory, "net-" + safeKey + ".jsonl");
            if (!File.Exists(netLogPath)) return false;

            var lines = File.ReadAllLines(netLogPath);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.IndexOf("\"scope\":\"local\"", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var portMatch = Regex.Match(line, "\"port\"\\s*:\\s*(?<port>\\d+)");
                if (!portMatch.Success || !int.TryParse(portMatch.Groups["port"].Value, out var port) || port <= 0)
                    continue;

                string? portFile = null;
                var fileMatch = Regex.Match(line, "\"portFile\"\\s*:\\s*\"(?<file>[^\"]+)\"");
                if (fileMatch.Success) portFile = fileMatch.Groups["file"].Value.Replace("/", "\\");

                isLocalScope = true;
                return !TcpPortInspector.IsPortActivelyListening(port, portFile);
            }
        }
        catch { }
        return false;
    }

    private static int GuessDependentLocalPort(CustomMcpServerEntry server, string? logText)
    {
        return 0;
    }

    public async Task ApplyAllowedRootsAsync(IEnumerable<string> enabledRoots)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var roots = NormalizeRoots(enabledRoots);
            _desiredRoots = roots;
            var dependencies = await Task.Run(() => McpDependencyResolver.Resolve()).ConfigureAwait(false);
            var routerNeedsStart = !IsRouterReady;
            if (routerNeedsStart)
            {
                await StopRouterForRestartAsync().ConfigureAwait(false);
                StaleManagedProcessCleaner.CleanupKnownOrphans();
            }

            await ReconcileFilesystemServerAsync(dependencies, roots, true).ConfigureAwait(false);

            if (routerNeedsStart && !dependencies.HasNode)
            {
                _gatewayStatus = BuildNodeDependencyUnavailableMessage(dependencies);
                PublishCurrentSnapshot();
                EnsureManagedProcessMonitorStarted();
                return;
            }

            await PushUpstreamsToRouterAsync().ConfigureAwait(false);

            if (routerNeedsStart)
            {
                await StartRootsFilterAsync(dependencies).ConfigureAwait(false);
                _routerReady = true;
                _nextRouterRetryUtc = DateTime.MinValue;
                StartPollCacheSubscription();
                await PushUpstreamsToRouterAsync().ConfigureAwait(false);
            }

            await EnsureBridgeMcpGatewayAsync(dependencies).ConfigureAwait(false);
            PublishCurrentSnapshot();
            EnsureManagedProcessMonitorStarted();
        }
        finally { _operationGate.Release(); }
    }

    public async Task<McpVerificationStepReport> VerifyCandidateAsync(
        CustomMcpServerEntry candidate,
        Action<McpVerificationStepReport>? onProgress = null,
        IEnumerable<string>? filesystemRoots = null,
        CustomMcpServerEntry? promoteAsOnSuccess = null)
    {
        ThrowIfDisposed();
        var report = new McpVerificationStepReport();
        var verificationId = "verify_" + Guid.NewGuid().ToString("N");
        var logId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        var lastStep1 = string.Empty;
        var lastStep2 = string.Empty;
        var lastStep3 = string.Empty;
        var lastStep4 = string.Empty;

        void Notify()
        {
            if (!string.Equals(lastStep1, report.Step1Preflight, StringComparison.Ordinal))
            {
                lastStep1 = report.Step1Preflight;
                ErrorLog.LogVerification("[" + logId + "] PRE-FLIGHT | " + lastStep1);
            }
            if (!string.Equals(lastStep2, report.Step2Transport, StringComparison.Ordinal))
            {
                lastStep2 = report.Step2Transport;
                ErrorLog.LogVerification("[" + logId + "] TRANSPORT | " + lastStep2);
            }
            if (!string.Equals(lastStep3, report.Step3Initialize, StringComparison.Ordinal))
            {
                lastStep3 = report.Step3Initialize;
                ErrorLog.LogVerification("[" + logId + "] MCP WORKER | " + lastStep3);
            }
            if (!string.Equals(lastStep4, report.Step4Tools, StringComparison.Ordinal))
            {
                lastStep4 = report.Step4Tools;
                ErrorLog.LogVerification("[" + logId + "] TOOLS | " + lastStep4);
            }
            if (report.IsDone)
            {
                ErrorLog.LogVerification("[" + logId + "] RESULT | " +
                    (report.IsSuccess ? "SUCCESS" : "FAILED") + " | " + report.Summary);
            }
            onProgress?.Invoke(report);
        }

        void SetFailure(string message, string? diagnostic = null)
        {
            report.IsDone = true;
            report.IsSuccess = false;
            report.Step3Initialize = I18n.Tr("Проверка Worker не пройдена: ", "Worker verification failed: ") + message;
            report.Step4Tools = I18n.Tr("Список инструментов не получен", "Tool list was not received");
            report.DiagnosticOutput = diagnostic ?? message;
            report.Summary = I18n.Tr("Сервер не прошёл проверку: ", "Server verification failed: ") + message;
            Notify();
        }

        var coreDependencies = await Task.Run(() => McpDependencyResolver.Resolve()).ConfigureAwait(false);
        if (!coreDependencies.HasNode && !IsRouterReady)
        {
            SetFailure(BuildNodeDependencyUnavailableMessage(coreDependencies));
            return report;
        }

        void ApplyCachedVerification(PollManagerVerificationResponse verification, CustomMcpServerEntry source)
        {
            var state = verification.State;
            report.IsSuccess = verification.Success && state != null &&
                string.Equals(state.Health, "online", StringComparison.OrdinalIgnoreCase);
            report.IsDone = true;
            report.DiscoveredServerName = verification.DiscoveredServerName ?? source.Name;
            report.ToolsCount = state?.ToolsCount ?? 0;
            report.ToolNames = verification.ToolNames ?? new List<string>();
            var isPending = state?.Pending == true;
            report.Step2Transport = report.IsSuccess
                ? I18n.Tr("OK: зарегистрированный Worker подключён", "OK: registered worker connected")
                : isPending
                    ? I18n.Tr("Ожидание локального процесса", "Waiting for local process")
                    : I18n.Tr("Ошибка зарегистрированного Worker", "Registered worker failed");
            report.Step3Initialize = report.IsSuccess
                ? I18n.Tr("OK: MCP discovery / initialize", "OK: MCP discovery / initialize")
                : isPending
                    ? I18n.Tr("Handshake начнётся после запуска процесса", "Handshake will start when the process launches")
                    : I18n.Tr("MCP handshake не завершён", "MCP handshake did not complete");
            report.Step4Tools = report.IsSuccess
                ? I18n.Tr("OK: инструментов: ", "OK: tools: ") + report.ToolsCount
                : isPending
                    ? I18n.Tr("Ожидание tools/list · последний каталог сохранён", "Waiting for tools/list · last catalog retained")
                    : I18n.Tr("tools/list не пройден", "tools/list failed");
            var diagnostic = state?.LastError ?? verification.Error;
            report.DiagnosticOutput = string.IsNullOrWhiteSpace(diagnostic)
                ? string.Empty
                : SanitizeUpstreamDiagnostic(diagnostic, source.Headers ?? source.Env);
            report.Summary = report.IsSuccess
                ? I18n.Tr("Верификация пройдена! Инструментов: ", "Verification passed! Tools: ") + report.ToolsCount
                : isPending
                    ? I18n.Tr("Сервер ожидает запуска локального процесса. Последний каталог инструментов сохранён.", "Server is waiting for its local process. The last tool catalog is retained.")
                    : I18n.Tr("Верификация не пройдена: ", "Verification failed: ") +
                      (string.IsNullOrWhiteSpace(report.DiagnosticOutput) ? "MCP Worker offline" : report.DiagnosticOutput);
        }

        ErrorLog.LogVerification("[" + logId + "] START | " + candidate.Name + " | transport=" + candidate.Transport);
        report.Step1Preflight = I18n.Tr("Проверка параметров и доверия...", "Validating configuration and trust...");
        Notify();

        var isHttp = string.Equals(candidate.Transport, "http", StringComparison.OrdinalIgnoreCase);
        if (isHttp)
        {
            var url = (candidate.Url ?? string.Empty).Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                SetFailure(I18n.Tr("Требуется корректный HTTP/HTTPS URL.", "A valid HTTP/HTTPS URL is required."));
                return report;
            }

            if (!McpRemoteEndpointTrust.IsTrusted(candidate, out var trustError))
            {
                SetFailure(trustError);
                return report;
            }

            var existingHttpWorkerId = FindExistingWorkerIdForCandidate(candidate, filesystemRoots);
            if (!string.IsNullOrWhiteSpace(existingHttpWorkerId))
            {
                report.Step1Preflight = I18n.Tr("OK: конфигурация уже зарегистрирована", "OK: configuration is already registered");
                report.Step2Transport = I18n.Tr("Подписка на существующий Worker…", "Subscribing to the existing worker…");
                report.Step3Initialize = I18n.Tr("Ожидание завершения текущей проверки", "Waiting for the current verification to complete");
                Notify();
                try
                {
                    var existingVerification = await _pollManagerClient.RequestVerificationAsync(existingHttpWorkerId!).ConfigureAwait(false);
                    ApplyCachedVerification(existingVerification, candidate);
                    Notify();
                    return report;
                }
                catch (Exception exception)
                {
                    var diagnostic = SanitizeUpstreamDiagnostic(
                        exception.Message,
                        candidate.Headers);
                    ErrorLog.Write("McpInfrastructureService.VerifyCandidateAsync.ExistingHttpWorker", exception);
                    SetFailure(
                        I18n.Tr(
                            "Не удалось получить результат существующего Worker: ",
                            "Could not receive the existing worker result: ") + diagnostic,
                        diagnostic);
                    return report;
                }
            }

            report.Step1Preflight = I18n.Tr("OK: URL и политика доверия проверены", "OK: URL and trust policy validated");
            report.Step2Transport = I18n.Tr(
                "Подписка на временный Worker Poll Manager…",
                "Subscribing to a temporary Poll Manager worker…");
            report.Step3Initialize = I18n.Tr(
                "Worker выполнит MCP discovery / initialize",
                "Worker will perform MCP discovery / initialize");
            report.Step4Tools = I18n.Tr(
                "Ожидание tools/list и публикации Cache",
                "Waiting for tools/list and Cache publication");
            Notify();

            try
            {
                var temporaryServer = new PollManagerVerificationServer
                {
                    Id = verificationId,
                    Name = string.IsNullOrWhiteSpace(candidate.Name) ? verificationId : candidate.Name,
                    Url = url,
                    Headers = candidate.Headers != null
                        ? new Dictionary<string, string>(candidate.Headers, StringComparer.OrdinalIgnoreCase)
                        : null,
                    Enabled = true,
                    Scope = uri.IsLoopback ? "local" : "remote",
                    Transport = "http"
                };
                var verification = await _pollManagerClient.VerifyTemporaryAsync(
                    temporaryServer,
                    preserveOnSuccess: promoteAsOnSuccess != null).ConfigureAwait(false);

                var state = verification.State;
                report.IsSuccess = verification.Success && state != null &&
                    string.Equals(state.Health, "online", StringComparison.OrdinalIgnoreCase);
                report.IsDone = true;
                report.DiscoveredServerName = verification.DiscoveredServerName ?? candidate.Name;
                report.ToolsCount = state?.ToolsCount ?? (verification.ToolNames?.Count ?? 0);
                report.ToolNames = verification.ToolNames ?? new List<string>();
                report.Step2Transport = report.IsSuccess
                    ? I18n.Tr("OK: временный HTTP Worker подключён", "OK: temporary HTTP worker connected")
                    : I18n.Tr("Ошибка временного HTTP Worker", "Temporary HTTP worker failed");
                report.Step3Initialize = report.IsSuccess
                    ? I18n.Tr("OK: MCP discovery / initialize", "OK: MCP discovery / initialize")
                    : I18n.Tr("MCP handshake не завершён", "MCP handshake did not complete");
                report.Step4Tools = report.IsSuccess
                    ? I18n.Tr("OK: инструментов: ", "OK: tools: ") + report.ToolsCount
                    : I18n.Tr("tools/list не пройден", "tools/list failed");
                var error = state?.LastError ?? verification.Error;
                report.DiagnosticOutput = string.IsNullOrWhiteSpace(error)
                    ? string.Empty
                    : SanitizeUpstreamDiagnostic(error, candidate.Headers);
                report.Summary = report.IsSuccess
                    ? I18n.Tr("Верификация пройдена! Инструментов: ", "Verification passed! Tools: ") + report.ToolsCount
                    : I18n.Tr("Верификация не пройдена: ", "Verification failed: ") +
                      (string.IsNullOrWhiteSpace(report.DiagnosticOutput) ? "MCP Worker offline" : report.DiagnosticOutput);

                if (report.IsSuccess && promoteAsOnSuccess != null)
                {
                    try
                    {
                        var permanent = new PollManagerVerificationServer
                        {
                            Id = promoteAsOnSuccess.Id,
                            Name = promoteAsOnSuccess.Name,
                            Url = url,
                            Headers = promoteAsOnSuccess.Headers != null
                                ? new Dictionary<string, string>(promoteAsOnSuccess.Headers, StringComparer.OrdinalIgnoreCase)
                                : null,
                            Enabled = true,
                            Scope = uri.IsLoopback ? "local" : "remote",
                            Transport = "http",
                            ConfigurationFingerprint = HashConfigurationFingerprint(
                                BuildHttpConfigurationFingerprint(url, promoteAsOnSuccess.Headers))
                        };
                        var promotion = await _pollManagerClient.PromoteTemporaryVerificationAsync(
                            verificationId,
                            permanent).ConfigureAwait(false);
                        if (!promotion.Promoted)
                            throw new InvalidOperationException(promotion.Error ?? "Poll Manager did not promote the verified worker.");
                    }
                    catch (Exception promotionException)
                    {
                        await _pollManagerClient.RemoveTemporaryVerificationAsync(verificationId).ConfigureAwait(false);
                        SetFailure(
                            SanitizeUpstreamDiagnostic(promotionException.Message, promoteAsOnSuccess.Headers),
                            SanitizeUpstreamDiagnostic(ErrorLog.Describe(promotionException), promoteAsOnSuccess.Headers));
                        return report;
                    }
                }

                Notify();
                return report;
            }
            catch (Exception exception)
            {
                SetFailure(
                    SanitizeUpstreamDiagnostic(exception.Message, candidate.Headers),
                    SanitizeUpstreamDiagnostic(ErrorLog.Describe(exception), candidate.Headers));
                return report;
            }
        }

        McpDependencySet dependenciesForStdio;
        string resolvedCommand;
        string effectiveCommand;
        List<string> effectiveArguments;
        Dictionary<string, string> enrichedEnvironment;
        int port;
        string endpoint;
        string configPath;
        string workingDirectory;

        try
        {
            dependenciesForStdio = coreDependencies;
            if (!dependenciesForStdio.HasNode)
            {
                SetFailure(BuildNodeDependencyUnavailableMessage(dependenciesForStdio));
                return report;
            }
            if (!dependenciesForStdio.HasSupergateway)
            {
                SetFailure(I18n.Tr(
                    "Проверка STDIO-сервера невозможна: требуется Supergateway 4.2.1 или новее.",
                    "STDIO server verification is unavailable: Supergateway 4.2.1 or newer is required."));
                return report;
            }
            if (string.Equals(candidate.Id, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase) &&
                !dependenciesForStdio.HasFilesystemMcp)
            {
                SetFailure(BuildFilesystemDependencyUnavailableMessage(dependenciesForStdio));
                return report;
            }

            var command = ResolveCommandExecutable(candidate.Command, candidate.WorkingDirectory, out var commandError);
            if (command == null)
            {
                SetFailure(commandError);
                return report;
            }
            resolvedCommand = command;

            report.Step1Preflight = I18n.Tr("OK: команда найдена", "OK: command resolved");
            report.Step2Transport = I18n.Tr("Подготовка временного STDIO-транспорта…", "Preparing temporary STDIO transport…");
            report.Step3Initialize = I18n.Tr("Ожидание проверки через Poll Manager Worker", "Waiting for Poll Manager worker verification");
            report.Step4Tools = I18n.Tr("Ожидание tools/list", "Waiting for tools/list");
            Notify();

            var verificationArguments = new List<string>();
            if (string.Equals(candidate.Id, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase))
            {
                verificationArguments.Add(dependenciesForStdio.FilesystemServerScript);
                verificationArguments.AddRange(candidate.Args ?? new List<string>());
                var roots = NormalizeRoots(filesystemRoots ?? Array.Empty<string>());
                var effectiveRoots = roots.Length == 0
                    ? new[] { AppPaths.EmptyFilesystemRootDirectory }
                    : roots;
                ValidateFilesystemExtraArguments(candidate.Args ?? new List<string>(), effectiveRoots);
                verificationArguments.AddRange(effectiveRoots);
            }
            else
            {
                verificationArguments.AddRange(candidate.Args ?? new List<string>());
            }

            effectiveCommand = PrepareStdioCommandAndArgs(
                dependenciesForStdio,
                resolvedCommand,
                verificationArguments,
                out effectiveArguments);
            enrichedEnvironment = BuildEnrichedEnvironment(dependenciesForStdio, candidate.Env, !string.IsNullOrWhiteSpace(candidate.Id) ? candidate.Id : verificationId);
            port = promoteAsOnSuccess != null ? AllocateFreePort(CustomPortRangeStart, CustomPortRangeEnd) : AllocateFreePort(VerifyPortRangeStart, VerifyPortRangeEnd);
            endpoint = LoopbackPrefix + port + "/" + verificationId + "/mcp";
            configPath = Path.Combine(AppPaths.CustomGatewaysDirectory, "verify-" + verificationId + ".json");
            workingDirectory = !string.IsNullOrWhiteSpace(candidate.WorkingDirectory) &&
                Directory.Exists(candidate.WorkingDirectory)
                ? candidate.WorkingDirectory!
                : AppPaths.InstallDirectory;
        }
        catch (Exception exception)
        {
            SetFailure(
                SanitizeUpstreamDiagnostic(exception.Message, candidate.Env),
                SanitizeUpstreamDiagnostic(ErrorLog.Describe(exception), candidate.Env));
            return report;
        }

        var existingStdioWorkerId = FindExistingWorkerIdForCandidate(candidate, filesystemRoots);
        if (!string.IsNullOrWhiteSpace(existingStdioWorkerId))
        {
            report.Step2Transport = I18n.Tr("Подписка на существующий STDIO Worker…", "Subscribing to the existing STDIO worker…");
            report.Step3Initialize = I18n.Tr("Ожидание завершения текущей проверки", "Waiting for the current verification to complete");
            Notify();
            try
            {
                var existingVerification = await _pollManagerClient.RequestVerificationAsync(existingStdioWorkerId!).ConfigureAwait(false);
                ApplyCachedVerification(existingVerification, candidate);
                if (_customStdioRuntimes.TryGetValue(existingStdioWorkerId!, out var existingRuntime))
                {
                    var output = existingRuntime.Child.GetOutput();
                    if (!string.IsNullOrWhiteSpace(output))
                        report.DiagnosticOutput = SanitizeUpstreamDiagnostic(output, candidate.Env);
                }
                Notify();
                return report;
            }
            catch (Exception exception)
            {
                var diagnostic = SanitizeUpstreamDiagnostic(
                    exception.Message,
                    candidate.Env);
                ErrorLog.Write("McpInfrastructureService.VerifyCandidateAsync.ExistingStdioWorker", exception);
                SetFailure(
                    I18n.Tr(
                        "Не удалось получить результат существующего Worker: ",
                        "Could not receive the existing worker result: ") + diagnostic,
                    diagnostic);
                return report;
            }
        }

        ManagedChild? temporaryGateway = null;

        try
        {
            Directory.CreateDirectory(AppPaths.CustomGatewaysDirectory);
            WriteJsonTextFile(
                configPath,
                BuildCustomStdioGatewayConfigJson(
                    port,
                    verificationId,
                    effectiveCommand,
                    effectiveArguments,
                    enrichedEnvironment,
                    candidate.AdditionalJson));

            temporaryGateway = StartChild(
                dependenciesForStdio.SupergatewayExecutable,
                "--config " + QuoteArgument(configPath),
                workingDirectory,
                "MCP-VERIFY-" + verificationId,
                enrichedEnvironment);

            var listener = await WaitForTcpListenerAsync(port, temporaryGateway, 15000).ConfigureAwait(false);
            if (!listener.Success)
            {
                SetFailure(
                    I18n.Tr("Временный транспорт не запустился: ", "Temporary transport failed to start: ") + listener.Message,
                    SanitizeUpstreamDiagnostic(temporaryGateway.GetOutput(), candidate.Env));
                return report;
            }

            report.Step2Transport = I18n.Tr("OK: локальный транспорт запущен", "OK: local transport started");
            report.Step3Initialize = I18n.Tr("ЗАГРУЗКА: скачивание пакета (uvx/npx) и ожидание MCP initialize…", "DOWNLOADING: downloading package (uvx/npx) & awaiting MCP initialize…");
            report.Step4Tools = I18n.Tr("Временный Worker запрашивает tools/list", "Temporary worker is requesting tools/list");
            Notify();

            var temporaryServer = new PollManagerVerificationServer
            {
                Id = verificationId,
                Name = string.IsNullOrWhiteSpace(candidate.Name) ? verificationId : candidate.Name,
                Url = endpoint,
                Enabled = true,
                Scope = "local",
                Transport = "stdio"
            };
            var verification = await _pollManagerClient.VerifyTemporaryAsync(
                temporaryServer,
                preserveOnSuccess: promoteAsOnSuccess != null).ConfigureAwait(false);

            var state = verification.State;
            var error = state?.LastError ?? verification.Error;
            report.ToolsCount = state?.ToolsCount ?? (verification.ToolNames?.Count ?? 0);
            report.ToolNames = verification.ToolNames ?? new List<string>();
            var toolsDiscovered = report.ToolsCount > 0;
            var isLocalAppOffline = toolsDiscovered &&
                !string.IsNullOrWhiteSpace(error) &&
                error!.IndexOf("ECONNREFUSED", StringComparison.OrdinalIgnoreCase) >= 0;

            report.IsSuccess = (verification.Success && state != null &&
                string.Equals(state.Health, "online", StringComparison.OrdinalIgnoreCase)) || isLocalAppOffline;
            report.IsDone = true;
            report.DiscoveredServerName = verification.DiscoveredServerName ?? candidate.Name;
            report.Step3Initialize = (report.IsSuccess || toolsDiscovered)
                ? I18n.Tr("OK: MCP discovery / initialize", "OK: MCP discovery / initialize")
                : I18n.Tr("MCP handshake не завершён", "MCP handshake did not complete");
            report.Step4Tools = (report.IsSuccess && !isLocalAppOffline)
                ? I18n.Tr("OK: инструментов: ", "OK: tools: ") + report.ToolsCount
                : toolsDiscovered
                    ? I18n.Tr("OK: инструментов: ", "OK: tools: ") + report.ToolsCount + I18n.Tr(" (приложение сейчас офлайн)", " (target app currently offline)")
                    : I18n.Tr("tools/list не пройден", "tools/list failed");
            var cleanError = string.IsNullOrWhiteSpace(error) ? string.Empty : SanitizeUpstreamDiagnostic(error, candidate.Env);
            var processOutput = temporaryGateway.GetOutput();
            var cleanOutput = string.IsNullOrWhiteSpace(processOutput) ? string.Empty : SanitizeUpstreamDiagnostic(processOutput, candidate.Env);
            if (!isHttp)
            {
                try
                {
                    var tempNetLog = Path.Combine(AppPaths.CustomGatewaysDirectory, "net-" + SettingsStore.SanitizeServerId(verificationId) + ".jsonl");
                    if (File.Exists(tempNetLog))
                    {
                        foreach (var keyCandidate in new[] { candidate.Id, SettingsStore.SanitizeServerId(candidate.Name) })
                        {
                            if (string.IsNullOrWhiteSpace(keyCandidate)) continue;
                            var permNetLog = Path.Combine(AppPaths.CustomGatewaysDirectory, "net-" + SettingsStore.SanitizeServerId(keyCandidate) + ".jsonl");
                            File.Copy(tempNetLog, permNetLog, true);
                            MarkServerPackageReady(keyCandidate);
                        }
                    }
                }
                catch { }
            }
            report.DiagnosticOutput = report.IsSuccess ? string.Empty : string.Join(Environment.NewLine, new[] { cleanError, cleanOutput }.Where(v => !string.IsNullOrWhiteSpace(v)));
            report.Summary = report.IsSuccess
                ? (isLocalAppOffline
                    ? I18n.Tr("Верификация пройдена! Инструментов: ", "Verification passed! Tools: ") + report.ToolsCount + I18n.Tr(" (целевое приложение сейчас офлайн)", " (target app is currently offline)")
                    : I18n.Tr("Верификация пройдена! Инструментов: ", "Verification passed! Tools: ") + report.ToolsCount)
                : I18n.Tr("Верификация не пройдена: ", "Verification failed: ") +
                  (!string.IsNullOrWhiteSpace(cleanError) ? cleanError : (!string.IsNullOrWhiteSpace(cleanOutput) ? cleanOutput : "MCP Worker offline"));

            if (report.IsSuccess && !isHttp) MarkServerPackageReady(!string.IsNullOrWhiteSpace(candidate.Id) ? candidate.Id : verificationId);

            if (report.IsSuccess && promoteAsOnSuccess != null)
            {
                try
                {
                    await _operationGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var permanent = new PollManagerVerificationServer
                        {
                            Id = promoteAsOnSuccess.Id,
                            Name = promoteAsOnSuccess.Name,
                            Url = endpoint,
                            Enabled = true,
                            Scope = "local",
                            Transport = "stdio",
                            ConfigurationFingerprint = HashConfigurationFingerprint(
                                BuildStdioFingerprint(promoteAsOnSuccess))
                        };
                        var promotion = await _pollManagerClient.PromoteTemporaryVerificationAsync(
                            verificationId,
                            permanent).ConfigureAwait(false);

                        if (!promotion.Promoted)
                            throw new InvalidOperationException(promotion.Error ?? "Poll Manager did not promote the verified worker.");

                        if (!_statusById.TryGetValue(promoteAsOnSuccess.Id, out var promotedStatus))
                        {
                            promotedStatus = new ServerRuntimeStatus { Id = promoteAsOnSuccess.Id, IsBuiltIn = false, TransportBadge = "STDIO -> HTTP" };
                            _statusById[promoteAsOnSuccess.Id] = promotedStatus;
                        }

                        promotedStatus.Name = promoteAsOnSuccess.Name;
                        promotedStatus.Subtitle = (promoteAsOnSuccess.Command ?? string.Empty) + " " + McpServerConfigParser.FormatArguments(promoteAsOnSuccess.Args);
                        promotedStatus.LastFingerprint = BuildStdioFingerprint(promoteAsOnSuccess);
                        promotedStatus.RuntimeStartFailed = false;
                        promotedStatus.IsEnabled = true;
                        promotedStatus.EndpointDisplay = port.ToString();
                        promotedStatus.PathDisplay = "/" + promoteAsOnSuccess.Id + "/mcp";
                        promotedStatus.UpstreamUrl = endpoint;
                        promotedStatus.HealthState = isLocalAppOffline ? McpServerHealthState.Offline : McpServerHealthState.Online;
                        promotedStatus.ToolsCount = report.ToolsCount;
                        promotedStatus.ToolsPreview = string.Join(", ", report.ToolNames.Take(6));
                        promotedStatus.StatusText = isLocalAppOffline
                            ? I18n.Tr("Сервер офлайн", "Server offline")
                            : I18n.Tr("Верифицирован и активен", "Verified & active");

                        if (temporaryGateway != null)
                        {
                            if (_customStdioRuntimes.ContainsKey(promoteAsOnSuccess.Id))
                            {
                                var existing = _customStdioRuntimes[promoteAsOnSuccess.Id];
                                _customStdioRuntimes.Remove(promoteAsOnSuccess.Id);
                                await StopChildAsync(existing.Child).ConfigureAwait(false);
                            }

                            _customStdioRuntimes[promoteAsOnSuccess.Id] = new ManagedCustomStdioRuntime(
                                promoteAsOnSuccess.Id,
                                port,
                                endpoint,
                                promotedStatus.LastFingerprint,
                                temporaryGateway);
                            temporaryGateway = null;
                        }
                    }
                    finally
                    {
                        _operationGate.Release();
                    }
                }
                catch (Exception promotionException)
                {
                    await _pollManagerClient.RemoveTemporaryVerificationAsync(verificationId).ConfigureAwait(false);
                    SetFailure(
                        SanitizeUpstreamDiagnostic(promotionException.Message, candidate.Env),
                        SanitizeUpstreamDiagnostic(ErrorLog.Describe(promotionException), candidate.Env));
                    return report;
                }
            }

            Notify();
            return report;
        }
        catch (Exception exception)
        {
            SetFailure(
                SanitizeUpstreamDiagnostic(exception.Message, candidate.Env),
                SanitizeUpstreamDiagnostic(ErrorLog.Describe(exception), candidate.Env));
            return report;
        }
        finally
        {
            lock (PortAllocationLock) { ReservedPortsInFlight.Remove(port); }
            if (temporaryGateway != null)
            {
                await StopChildAsync(temporaryGateway).ConfigureAwait(false);
                await WaitForPortReleaseAsync(port, 3000).ConfigureAwait(false);
                try
                {
                    if (File.Exists(configPath)) File.Delete(configPath);
                }
                catch (Exception exception)
                {
                    ErrorLog.Write("McpInfrastructureService.VerifyCandidateAsync.Cleanup", exception);
                }
            }
        }
    }
    private async Task ReconcileFilesystemServerAsync(McpDependencySet dependencies, string[] roots, bool forceRestartIfChanged)
    {
        const string id = SettingsStore.BuiltInFilesystemId;
        if (_filesystemDeleted)
        {
            if (_filesystemGateway != null)
            {
                await StopChildAsync(_filesystemGateway).ConfigureAwait(false);
                _filesystemGateway = null;
            }
            _activeFilesystemFingerprint = string.Empty;
            _statusById.Remove(id);
            return;
        }

        if (!_statusById.TryGetValue(id, out var status))
        {
            status = new ServerRuntimeStatus
            {
                Id = id,
                Name = "Filesystem MCP",
                Subtitle = I18n.Tr("Доступ к отмеченным галочками папкам проектов", "Access to checked project folders"),
                IsBuiltIn = true,
                TransportBadge = "STDIO -> HTTP",
                EndpointDisplay = "8003",
                PathDisplay = "/filesystem/mcp",
                UpstreamUrl = FilesystemEndpoint
            };
            _statusById[id] = status;
        }

        status.Name = (_filesystemCustom != null && !string.IsNullOrWhiteSpace(_filesystemCustom.Name))
            ? _filesystemCustom.Name!
            : "Filesystem MCP";
        status.Subtitle = I18n.Tr("Доступ к отмеченным галочками папкам проектов", "Access to checked project folders");
        var desiredFsEnabled = IsDesiredEnabled(id, _filesystemEnabled);
        status.IsEnabled = desiredFsEnabled;

        if (!desiredFsEnabled)
        {
            if (_filesystemGateway != null)
            {
                await StopChildAsync(_filesystemGateway).ConfigureAwait(false);
                _filesystemGateway = null;
                await WaitForPortReleaseAsync(FilesystemPort, 3000).ConfigureAwait(false);
                ErrorLog.LogConnection(I18n.Tr("Filesystem MCP (:8003) остановлен пользователем.", "Filesystem MCP (:8003) stopped by user."));
            }
            _activeRoots = Array.Empty<string>();
            _activeFilesystemFingerprint = string.Empty;
            if (IsDesiredEnabled(id, _filesystemEnabled)) return;
            ApplyDesiredEnabledToStatus(status, false);
            _filesystemStatus = status.StatusText;
            return;
        }

        if (!dependencies.HasNode || !dependencies.HasFilesystemMcp || !dependencies.HasSupergateway)
        {
            if (_filesystemGateway != null)
            {
                await StopChildAsync(_filesystemGateway).ConfigureAwait(false);
                _filesystemGateway = null;
                await WaitForPortReleaseAsync(FilesystemPort, 3000).ConfigureAwait(false);
            }

            _activeRoots = Array.Empty<string>();
            _activeFilesystemFingerprint = string.Empty;
            status.HealthState = McpServerHealthState.Quarantine;
            status.ToolsCount = 0;
            status.ToolsPreview = string.Empty;
            status.LastDiagnosticLog = string.Empty;
            status.StatusText = BuildFilesystemDependencyUnavailableMessage(dependencies);
            _filesystemStatus = status.StatusText;
            return;
        }

        var alive = _filesystemGateway != null && !IsExited(_filesystemGateway.Process);
        var rootsMatch = _activeRoots.SequenceEqual(roots, StringComparer.OrdinalIgnoreCase);
        var configurationFingerprint = BuildFilesystemFingerprint(_filesystemCustom, roots);
        var configurationMatches = string.Equals(_activeFilesystemFingerprint, configurationFingerprint, StringComparison.Ordinal);
        var configurationTransition = GetConfigurationTransition(id, configurationFingerprint);
        if (alive && rootsMatch && configurationMatches && !forceRestartIfChanged)
            return;

        if (_filesystemGateway != null)
        {
            await StopChildAsync(_filesystemGateway).ConfigureAwait(false);
            _filesystemGateway = null;
            await WaitForPortReleaseAsync(FilesystemPort, 4000).ConfigureAwait(false);
        }

        try
        {
            await StartFilesystemGatewayAsync(dependencies, roots).ConfigureAwait(false);
            if (!IsDesiredEnabled(id, _filesystemEnabled))
            {
                if (_filesystemGateway != null)
                {
                    await StopChildAsync(_filesystemGateway).ConfigureAwait(false);
                    _filesystemGateway = null;
                    await WaitForPortReleaseAsync(FilesystemPort, 3000).ConfigureAwait(false);
                }
                _activeRoots = Array.Empty<string>();
                _activeFilesystemFingerprint = string.Empty;
                ApplyDesiredEnabledToStatus(status, false);
                _filesystemStatus = status.StatusText;
                return;
            }
            _activeRoots = roots;
            _activeFilesystemFingerprint = configurationFingerprint;
            status.UpstreamUrl = FilesystemEndpoint;
            var requiresNewVerification =
                status.HealthState == McpServerHealthState.Disabled ||
                status.HealthState == McpServerHealthState.Quarantine ||
                !rootsMatch ||
                !configurationMatches ||
                configurationTransition != ConfigurationTransition.Unchanged;
            if (requiresNewVerification)
            {
                status.HealthState = McpServerHealthState.Waiting;
                status.ToolsCount = 0;
                status.ToolsPreview = string.Empty;
                status.StatusText = GetConfigurationTransitionStatus(configurationTransition);
            }
            else if (status.HealthState == McpServerHealthState.Waiting)
            {
                status.StatusText = I18n.Tr(
                    "Ожидание проверки MCP endpoint…",
                    "Waiting for MCP endpoint verification…");
            }
            status.LastDiagnosticLog = string.Empty;
            _filesystemStatus = status.StatusText;
            ErrorLog.LogConnection(I18n.Tr(
                "Filesystem MCP (:8003) запущен. MCP-проверку выполняет Poll Manager.",
                "Filesystem MCP (:8003) started. MCP verification is owned by Poll Manager."));
        }
        catch (Exception exception)
        {
            if (_filesystemGateway != null)
            {
                await StopChildAsync(_filesystemGateway).ConfigureAwait(false);
                _filesystemGateway = null;
                await WaitForPortReleaseAsync(FilesystemPort, 3000).ConfigureAwait(false);
            }
            ErrorLog.Write("McpInfrastructureService.ReconcileFilesystemServerAsync", exception);
            _activeFilesystemFingerprint = string.Empty;
            status.HealthState = McpServerHealthState.Quarantine;
            status.StatusText = I18n.Tr("Ошибка запуска Filesystem MCP: ", "Filesystem MCP startup error: ") + exception.Message;
            status.ToolsCount = 0;
            status.LastDiagnosticLog = exception.ToString();
            _filesystemStatus = status.StatusText;
        }
    }

    private Task<bool> ReconcileUnrealServerAsync()
    {
        const string id = SettingsStore.BuiltInUnrealId;
        if (_unrealDeleted)
        {
            _statusById.Remove(id);
            return Task.FromResult(true);
        }

        var targetUrl = (_unrealCustom != null && !string.IsNullOrWhiteSpace(_unrealCustom.Url))
            ? _unrealCustom.Url!
            : UnrealEndpoint;
        var displayName = (_unrealCustom != null && !string.IsNullOrWhiteSpace(_unrealCustom.Name))
            ? _unrealCustom.Name!
            : "Unreal MCP";
        var targetPort = 8000;
        var targetPath = "/mcp";
        if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var parsedUri))
        {
            targetPort = parsedUri.Port;
            targetPath = parsedUri.AbsolutePath;
        }

        if (!_statusById.TryGetValue(id, out var status))
        {
            status = new ServerRuntimeStatus { Id = id, IsBuiltIn = true, TransportBadge = "HTTP / AUTO" };
            _statusById[id] = status;
        }

        var previousUrl = status.UpstreamUrl;
        var previousName = status.Name;
        var previousEnabled = status.IsEnabled;
        status.Name = displayName;
        status.Subtitle = I18n.Tr("Автоподключение к Unreal Editor (:", "Auto-attach to Unreal Editor (:") + targetPort + ")";
        status.EndpointDisplay = targetPort.ToString();
        status.PathDisplay = targetPath;
        status.UpstreamUrl = targetUrl;
        status.IsEnabled = _unrealEnabled;

        if (!_unrealEnabled)
        {
            status.HealthState = McpServerHealthState.Disabled;
            status.StatusText = I18n.Tr("Выключен", "Disabled");
            status.ToolsCount = 0;
            status.ToolsPreview = string.Empty;
            _unrealStatus = status.StatusText;
            return Task.FromResult(previousEnabled || previousUrl != targetUrl);
        }

        if (_unrealCustom != null && !string.IsNullOrWhiteSpace(_unrealCustom.Url) &&
            !McpRemoteEndpointTrust.IsTrusted(_unrealCustom, out var trustError))
        {
            status.HealthState = McpServerHealthState.Quarantine;
            status.StatusText = trustError;
            status.ToolsCount = 0;
            status.ToolsPreview = string.Empty;
            _unrealStatus = status.StatusText;
            return Task.FromResult(previousEnabled != status.IsEnabled || previousUrl != targetUrl);
        }

        var configurationChanged =
            !string.Equals(previousUrl, targetUrl, StringComparison.Ordinal) ||
            !string.Equals(previousName, displayName, StringComparison.Ordinal) ||
            !previousEnabled;
        if (configurationChanged || status.HealthState == McpServerHealthState.Disabled ||
            status.HealthState == McpServerHealthState.Quarantine)
        {
            status.HealthState = McpServerHealthState.Waiting;
            status.ToolsCount = 0;
            status.ToolsPreview = string.Empty;
            status.StatusText = I18n.Tr(
                "Ожидание независимой проверки MCP Worker",
                "Waiting for independent MCP worker verification");
        }

        _unrealStatus = status.StatusText;
        return Task.FromResult(configurationChanged);
    }

    private async Task ReconcileCustomServersAsync(
        McpDependencySet dependencies,
        bool forceRetryQuarantined,
        string? forceRetryServerId)
    {
        var desiredIds = new HashSet<string>(_customServers.Select(server => server.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var existingId in _customStdioRuntimes.Keys.ToArray())
        {
            if (!desiredIds.Contains(existingId))
            {
                await StopCustomStdioServerAsync(existingId).ConfigureAwait(false);
                DeleteServerArtifacts(existingId);
            }
        }

        lock (_stateLock)
        {
            foreach (var existingStatusId in _statusById.Keys.ToArray())
            {
                if (!string.Equals(existingStatusId, SettingsStore.BuiltInUnrealId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(existingStatusId, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase) &&
                    !desiredIds.Contains(existingStatusId))
                {
                    _statusById.Remove(existingStatusId);
                    DeleteServerArtifacts(existingStatusId);
                }
            }
        }

        CleanupOrphanedServerPackages();
        PublishCurrentSnapshot();

        var tasks = _customServers.Select(async server =>
        {
            if (string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase))
            {
                await StopCustomStdioServerAsync(server.Id).ConfigureAwait(false);
                await ReconcileCustomHttpServerAsync(server).ConfigureAwait(false);
            }
            else
            {
                var forceThis = (forceRetryQuarantined &&
                    _statusById.TryGetValue(server.Id, out var serverStatus) &&
                    serverStatus.HealthState == McpServerHealthState.Quarantine) ||
                    string.Equals(forceRetryServerId, server.Id, StringComparison.OrdinalIgnoreCase);
                await ReconcileCustomStdioServerAsync(dependencies, server, forceThis).ConfigureAwait(false);
            }
            PublishCurrentSnapshot();
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private Task<bool> ReconcileCustomHttpServerAsync(CustomMcpServerEntry server)
    {
        if (!_statusById.TryGetValue(server.Id, out var status))
        {
            status = new ServerRuntimeStatus { Id = server.Id, IsBuiltIn = false };
            _statusById[server.Id] = status;
        }

        var previousName = status.Name;
        var previousUrl = status.UpstreamUrl;
        var previousEnabled = status.IsEnabled;
        status.Name = server.Name;
        status.Subtitle = I18n.Tr("Внешний MCP · ", "External MCP · ") + (server.Url ?? string.Empty);
        status.TransportBadge = "HTTP / AUTO";
        status.IsEnabled = server.Enabled;
        status.UpstreamUrl = server.Url ?? string.Empty;

        if (Uri.TryCreate(server.Url, UriKind.Absolute, out var uri))
        {
            status.EndpointDisplay = uri.Port.ToString();
            status.PathDisplay = uri.AbsolutePath;
        }
        else
        {
            status.EndpointDisplay = "HTTP";
            status.PathDisplay = server.Url ?? string.Empty;
        }

        if (!server.Enabled)
        {
            status.HealthState = McpServerHealthState.Disabled;
            status.StatusText = I18n.Tr("Выключен", "Disabled");
            status.ToolsCount = 0;
            status.ToolsPreview = string.Empty;
            return Task.FromResult(previousEnabled || previousUrl != status.UpstreamUrl);
        }

        if (uri == null ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            status.HealthState = McpServerHealthState.Quarantine;
            status.StatusText = I18n.Tr("Некорректный URL: требуется HTTP или HTTPS", "Invalid URL: HTTP or HTTPS is required");
            status.ToolsCount = 0;
            status.ToolsPreview = string.Empty;
            return Task.FromResult(previousUrl != status.UpstreamUrl || previousEnabled != status.IsEnabled);
        }

        if (!McpRemoteEndpointTrust.IsTrusted(server, out var trustError))
        {
            status.HealthState = McpServerHealthState.Quarantine;
            status.StatusText = trustError;
            status.ToolsCount = 0;
            status.ToolsPreview = string.Empty;
            return Task.FromResult(previousUrl != status.UpstreamUrl || previousEnabled != status.IsEnabled);
        }

        var configurationChanged =
            !string.Equals(previousName, status.Name, StringComparison.Ordinal) ||
            !string.Equals(previousUrl, status.UpstreamUrl, StringComparison.Ordinal) ||
            !previousEnabled ||
            status.HealthState == McpServerHealthState.Disabled ||
            status.HealthState == McpServerHealthState.Quarantine;
        if (configurationChanged)
        {
            status.HealthState = McpServerHealthState.Waiting;
            status.ToolsCount = 0;
            status.ToolsPreview = string.Empty;
            status.StatusText = I18n.Tr(
                "Ожидание независимой проверки MCP Worker",
                "Waiting for independent MCP worker verification");
        }

        return Task.FromResult(configurationChanged);
    }

    private async Task ReconcileCustomStdioServerAsync(McpDependencySet dependencies, CustomMcpServerEntry server, bool forceRetry)
    {
        if (!_statusById.TryGetValue(server.Id, out var status))
        {
            status = new ServerRuntimeStatus { Id = server.Id, IsBuiltIn = false };
            _statusById[server.Id] = status;
        }
        var fingerprint = BuildStdioFingerprint(server);
        if (forceRetry || !string.Equals(status.LastFingerprint, fingerprint, StringComparison.Ordinal))
            status.RuntimeStartFailed = false;
        status.LastFingerprint = fingerprint;
        status.Name = server.Name;
        status.Subtitle = (server.Command ?? string.Empty) + " " + McpServerConfigParser.FormatArguments(server.Args);
        status.TransportBadge = "STDIO -> HTTP";
        var desiredStdioEnabled = IsDesiredEnabled(server.Id, server.Enabled);
        status.IsEnabled = desiredStdioEnabled;
        if (!desiredStdioEnabled)
        {
            status.RuntimeStartFailed = false;
            await StopCustomStdioServerAsync(server.Id).ConfigureAwait(false);
            if (IsDesiredEnabled(server.Id, server.Enabled)) return;
            status.PathDisplay = "/" + server.Id + "/mcp";
            ApplyDesiredEnabledToStatus(status, false);
            return;
        }

        var knownDepPort = status.DependentLocalPort > 0 ? status.DependentLocalPort : GuessDependentLocalPort(server, status.LastDiagnosticLog);
        if (knownDepPort > 0)
        {
            status.DependentLocalPort = knownDepPort;
            if (TcpPortInspector.GetListeningProcessIds(knownDepPort).Count == 0)
            {
                await StopCustomStdioServerAsync(server.Id).ConfigureAwait(false);
                status.EndpointDisplay = ":" + knownDepPort;
                status.PathDisplay = "/" + server.Id + "/mcp";
                status.HealthState = McpServerHealthState.Offline;
                status.ToolsCount = 0;
                status.ToolsPreview = string.Empty;
                status.StatusText = I18n.Tr("Сервер офлайн", "Server offline");
                return;
            }
        }

        if (_customStdioRuntimes.TryGetValue(server.Id, out var existingRuntime))
        {
            if (!IsExited(existingRuntime.Child.Process) &&
                string.Equals(existingRuntime.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                status.EndpointDisplay = existingRuntime.Port.ToString();
                status.PathDisplay = new Uri(existingRuntime.EndpointUrl).AbsolutePath;
                status.UpstreamUrl = existingRuntime.EndpointUrl;
                status.RuntimeStartFailed = false;
                if (status.HealthState == McpServerHealthState.Disabled ||
                    status.HealthState == McpServerHealthState.Quarantine)
                {
                    status.HealthState = McpServerHealthState.Waiting;
                    status.StatusText = I18n.Tr(
                        "Процесс активен · ожидание данных Poll Manager Cache",
                        "Process is active · waiting for Poll Manager cache state");
                }
                return;
            }
            await StopCustomStdioServerAsync(server.Id).ConfigureAwait(false);
        }
        if (status.RuntimeStartFailed && !forceRetry)
            return;

        if (!dependencies.HasNode || !dependencies.HasSupergateway)
        {
            status.RuntimeStartFailed = true;
            status.HealthState = McpServerHealthState.Quarantine;
            status.EndpointDisplay = "—";
            status.PathDisplay = "Dependency";
            status.ToolsCount = 0;
            status.ToolsPreview = string.Empty;
            status.StatusText = !dependencies.HasNode
                ? BuildNodeDependencyUnavailableMessage(dependencies)
                : I18n.Tr(
                    "Локальный STDIO-сервер недоступен: требуется Supergateway 4.2.1 или новее.",
                    "Local STDIO server is unavailable: Supergateway 4.2.1 or newer is required.");
            return;
        }

        var resolvedCmd = ResolveCommandExecutable(server.Command, server.WorkingDirectory, out var cmdError);
        if (resolvedCmd == null)
        {
            status.RuntimeStartFailed = true;
            status.HealthState = McpServerHealthState.Quarantine;
            status.EndpointDisplay = "ERR";
            status.PathDisplay = "Pre-flight";
            status.StatusText = cmdError;
            return;
        }
        var port = AllocateFreePort(CustomPortRangeStart, CustomPortRangeEnd);
        var configPath = AppPaths.GetCustomGatewayConfigPath(server.Id);
        var endpoint = LoopbackPrefix + port + "/" + server.Id + "/mcp";
        status.EndpointDisplay = port.ToString();
        status.PathDisplay = "/" + server.Id + "/mcp";
        status.UpstreamUrl = endpoint;

        var effectiveCmd = PrepareStdioCommandAndArgs(dependencies, resolvedCmd, server.Args ?? new List<string>(), out var effectiveArgs);
        var enrichedEnv = BuildEnrichedEnvironment(dependencies, server.Env, server.Id);
        WriteJsonTextFile(configPath, BuildCustomStdioGatewayConfigJson(port, server.Id, effectiveCmd, effectiveArgs, enrichedEnv, server.AdditionalJson));
        var workDir = !string.IsNullOrWhiteSpace(server.WorkingDirectory) && Directory.Exists(server.WorkingDirectory) ? server.WorkingDirectory! : AppPaths.InstallDirectory;

        ManagedChild? child = null;
        try
        {
            child = StartChild(dependencies.SupergatewayExecutable, "--config " + QuoteArgument(configPath), workDir, "MCP-" + server.Id, enrichedEnv);
            var listener = await WaitForTcpListenerAsync(port, child, 15000).ConfigureAwait(false);
            if (!listener.Success)
            {
                await StopChildAsync(child).ConfigureAwait(false);
                status.RuntimeStartFailed = true;
                status.HealthState = McpServerHealthState.Quarantine;
                status.StatusText = I18n.Tr("Карантин (сбой старта): ", "Quarantine (startup failed): ") + listener.Message;
                return;
            }
            status.LastDiagnosticLog = child.GetOutput();
            var detectedDependencyPort = GuessDependentLocalPort(server, status.LastDiagnosticLog);
            if (detectedDependencyPort > 0) status.DependentLocalPort = detectedDependencyPort;

            
            if (!IsDesiredEnabled(server.Id, server.Enabled))
            {
                lock (PortAllocationLock) { ReservedPortsInFlight.Remove(port); }
                await StopChildAsync(child).ConfigureAwait(false);
                await WaitForPortReleaseAsync(port, 3000).ConfigureAwait(false);
                ApplyDesiredEnabledToStatus(status, false);
                return;
            }
            lock (_stateLock) { _customStdioRuntimes[server.Id] = new ManagedCustomStdioRuntime(server.Id, port, endpoint, fingerprint, child); } lock (PortAllocationLock) { ReservedPortsInFlight.Remove(port); }
            status.RuntimeStartFailed = false;
            var pkgReady = IsServerPackageReady(server.Id);
            status.HealthState = pkgReady ? McpServerHealthState.Waiting : McpServerHealthState.Downloading;
            status.ToolsCount = 0;
            status.ToolsPreview = string.Empty;
            status.StatusText = pkgReady
                ? I18n.Tr("Установка связи MCP…", "Establishing MCP connection…")
                : I18n.Tr("Скачивание и запуск пакета MCP…", "Downloading & starting MCP package…");
            ErrorLog.LogConnection(I18n.Tr(
                "Локальный транспорт MCP для «" + server.Name + "» (:" + port + ") запущен. Проверку выполняет Poll Manager.",
                "Local MCP transport for '" + server.Name + "' (:" + port + ") started. Poll Manager owns verification."));
        }
        catch (Exception ex)
        {
            if (child != null) await StopChildAsync(child).ConfigureAwait(false);
            status.RuntimeStartFailed = true;
            status.HealthState = McpServerHealthState.Quarantine;
            status.StatusText = I18n.Tr("Карантин: ", "Quarantine: ") + ex.Message;
            ErrorLog.Write("McpInfrastructureService.ReconcileCustomStdioServerAsync." + server.Id, ex);
        }
        finally
        {
            lock (PortAllocationLock) { ReservedPortsInFlight.Remove(port); }
        }
    }

    private async Task StopCustomStdioServerAsync(string serverId)
    {
        ManagedCustomStdioRuntime? runtime;
        lock (_stateLock)
        {
            if (!_customStdioRuntimes.TryGetValue(serverId, out runtime)) return;
            _customStdioRuntimes.Remove(serverId);
        }
        await StopChildAsync(runtime!.Child).ConfigureAwait(false);
        await WaitForPortReleaseAsync(runtime!.Port, 3000).ConfigureAwait(false);
    }

    private ConfigurationTransition GetConfigurationTransition(string serverId, string fingerprint)
    {
        var normalizedFingerprint = HashConfigurationFingerprint(fingerprint);
        lock (_stateLock)
        {
            if (!_lastPublishedConfigurationFingerprints.TryGetValue(serverId, out var previousFingerprint))
                return ConfigurationTransition.Initial;

            return string.Equals(previousFingerprint, normalizedFingerprint, StringComparison.Ordinal)
                ? ConfigurationTransition.Unchanged
                : ConfigurationTransition.Changed;
        }
    }

    private void RecordPublishedConfigurationFingerprints(IEnumerable<RouterUpstreamItem> configuredUpstreams)
    {
        var items = configuredUpstreams.ToList();
        var configuredIds = new HashSet<string>(
            items.Where(item => !string.IsNullOrWhiteSpace(item.Id)).Select(item => item.Id),
            StringComparer.OrdinalIgnoreCase);

        lock (_stateLock)
        {
            foreach (var removedId in _lastPublishedConfigurationFingerprints.Keys
                         .Where(serverId => !configuredIds.Contains(serverId))
                         .ToArray())
            {
                _lastPublishedConfigurationFingerprints.Remove(removedId);
            }

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Id) ||
                    !item.Enabled ||
                    item.Pending ||
                    string.IsNullOrWhiteSpace(item.ConfigurationFingerprint))
                {
                    // Keep the last applied fingerprint while a known server is disabled or pending.
                    continue;
                }

                _lastPublishedConfigurationFingerprints[item.Id] = item.ConfigurationFingerprint;
            }
        }
    }

    private static string GetConfigurationTransitionStatus(ConfigurationTransition transition)
    {
        return transition switch
        {
            ConfigurationTransition.Initial => I18n.Tr(
                "Первичная инициализация MCP · проверка endpoint…",
                "Initial MCP initialization · verifying endpoint…"),
            ConfigurationTransition.Changed => I18n.Tr(
                "Конфигурация MCP изменена · проверка endpoint…",
                "MCP configuration changed · verifying endpoint…"),
            _ => I18n.Tr(
                "Восстановление MCP · проверка endpoint…",
                "Recovering MCP · verifying endpoint…")
        };
    }

    private async Task<bool> PushUpstreamsToRouterAsync()
    {
        var configuredUpstreams = new List<RouterUpstreamItem>();

        void AddUpstream(
            string id,
            string name,
            string url,
            IDictionary<string, string>? headers,
            string transport,
            string fingerprint,
            bool enabled = true,
            bool pending = false,
            string? explicitScope = null,
            bool localTargetOffline = false)
        {
            Uri? uri = null;
            if (enabled && !pending)
            {
                if (string.IsNullOrWhiteSpace(url) ||
                    !Uri.TryCreate(url, UriKind.Absolute, out uri))
                    return;

                if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    return;

                if (!uri.IsLoopback &&
                    !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            else if (!string.IsNullOrWhiteSpace(url))
            {
                Uri.TryCreate(url, UriKind.Absolute, out uri);
            }

            var resolvedScope = !string.IsNullOrWhiteSpace(explicitScope)
                ? explicitScope!
                : (uri != null && !uri.IsLoopback ? "remote" : "local");
            configuredUpstreams.Add(new RouterUpstreamItem
            {
                Id = id,
                Name = name,
                Url = url ?? string.Empty,
                Headers = headers != null && headers.Count > 0
                    ? new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
                    : null,
                Enabled = enabled,
                Pending = pending,
                Scope = resolvedScope,
                Transport = transport,
                ConfigurationFingerprint = HashConfigurationFingerprint(fingerprint),
                LocalTargetOffline = localTargetOffline
            });
        }

        if (!_unrealDeleted)
        {
            var unrealName = _unrealCustom != null && !string.IsNullOrWhiteSpace(_unrealCustom.Name)
                ? _unrealCustom.Name!
                : "Unreal MCP";
            var isUnrealStdio = _unrealCustom != null &&
                !string.Equals(_unrealCustom.Transport, "http", StringComparison.OrdinalIgnoreCase);

            if (isUnrealStdio)
            {
                if (!_unrealEnabled)
                {
                    var disabledUrl = _customStdioRuntimes.TryGetValue(SettingsStore.BuiltInUnrealId, out var disabledRuntime)
                        ? disabledRuntime.EndpointUrl
                        : string.Empty;
                    AddUpstream(SettingsStore.BuiltInUnrealId, unrealName, disabledUrl, null, "stdio", BuildStdioFingerprint(_unrealCustom!), false);
                }
                else if (_customStdioRuntimes.TryGetValue(SettingsStore.BuiltInUnrealId, out var uRuntime) && !IsExited(uRuntime.Child.Process))
                {
                    var isLocalTargetOffline = CheckIfStdioLocalTargetOffline(SettingsStore.BuiltInUnrealId, out var detectedLocalScope);
                    var scope = detectedLocalScope ? "local" : DetermineServerScope("stdio", null, _unrealCustom!.Command, _unrealCustom.Args, _unrealCustom.Env, null, _unrealCustom.AdditionalJson);
                    AddUpstream(SettingsStore.BuiltInUnrealId, unrealName, uRuntime.EndpointUrl, null, "stdio", uRuntime.Fingerprint, true, false, scope, isLocalTargetOffline);
                }
                else
                {
                    AddUpstream(SettingsStore.BuiltInUnrealId, unrealName, string.Empty, null, "stdio", BuildStdioFingerprint(_unrealCustom!), true, true);
                }
            }
            else
            {
                var unrealUrl = _unrealCustom != null && !string.IsNullOrWhiteSpace(_unrealCustom.Url)
                    ? _unrealCustom.Url!
                    : UnrealEndpoint;

                if (!_unrealEnabled ||
                    _unrealCustom == null ||
                    string.IsNullOrWhiteSpace(_unrealCustom.Url) ||
                    McpRemoteEndpointTrust.IsTrusted(_unrealCustom, out _))
                {
                    AddUpstream(
                        SettingsStore.BuiltInUnrealId,
                        unrealName,
                        unrealUrl,
                        _unrealCustom?.Headers,
                        "http",
                        BuildHttpConfigurationFingerprint(unrealUrl, _unrealCustom?.Headers),
                        _unrealEnabled);
                }
            }
        }

        if (!_filesystemDeleted)
        {
            var filesystemName = _filesystemCustom != null && !string.IsNullOrWhiteSpace(_filesystemCustom.Name)
                ? _filesystemCustom.Name!
                : "Filesystem MCP";

            if (_filesystemEnabled &&
                _filesystemGateway != null &&
                !IsExited(_filesystemGateway.Process))
            {
                AddUpstream(
                    SettingsStore.BuiltInFilesystemId,
                    filesystemName,
                    FilesystemEndpoint,
                    null,
                    "stdio",
                    _activeFilesystemFingerprint);
            }
            else if (!_filesystemEnabled)
            {
                AddUpstream(
                    SettingsStore.BuiltInFilesystemId,
                    filesystemName,
                    FilesystemEndpoint,
                    null,
                    "stdio",
                    BuildFilesystemFingerprint(_filesystemCustom, _activeRoots),
                    false);
            }
            else
            {
                AddUpstream(
                    SettingsStore.BuiltInFilesystemId,
                    filesystemName,
                    string.Empty,
                    null,
                    "stdio",
                    BuildFilesystemFingerprint(_filesystemCustom, _activeRoots),
                    true,
                    true);
            }
        }

        foreach (var configured in _customServers)
        {
            var isHttp = string.Equals(configured.Transport, "http", StringComparison.OrdinalIgnoreCase);
            var transport = isHttp ? "http" : "stdio";
            var depPort = _statusById.TryGetValue(configured.Id, out var st) && st.DependentLocalPort > 0
                ? st.DependentLocalPort
                : GuessDependentLocalPort(configured, st?.LastDiagnosticLog);
            var customScope = DetermineServerScope(
                transport,
                configured.Url,
                configured.Command,
                configured.Args,
                configured.Env,
                configured.Headers,
                configured.AdditionalJson,
                depPort);

            if (!configured.Enabled)
            {
                var disabledUrl = isHttp
                    ? configured.Url ?? string.Empty
                    : (_customStdioRuntimes.TryGetValue(configured.Id, out var disabledRuntime)
                        ? disabledRuntime.EndpointUrl
                        : string.Empty);
                var disabledFingerprint = isHttp
                    ? BuildHttpConfigurationFingerprint(disabledUrl, configured.Headers)
                    : BuildStdioFingerprint(configured);

                AddUpstream(
                    configured.Id,
                    configured.Name,
                    disabledUrl,
                    isHttp ? configured.Headers : null,
                    transport,
                    disabledFingerprint,
                    false,
                    false,
                    customScope);
                continue;
            }

            if (isHttp)
            {
                if (!McpRemoteEndpointTrust.IsTrusted(configured, out _)) continue;
                AddUpstream(
                    configured.Id,
                    configured.Name,
                    configured.Url ?? string.Empty,
                    configured.Headers,
                    "http",
                    BuildHttpConfigurationFingerprint(configured.Url ?? string.Empty, configured.Headers),
                    true,
                    false,
                    customScope);
                continue;
            }

            if (!_customStdioRuntimes.TryGetValue(configured.Id, out var runtime) ||
                IsExited(runtime!.Child.Process))
            {
                AddUpstream(
                    configured.Id,
                    configured.Name,
                    string.Empty,
                    null,
                    transport,
                    BuildStdioFingerprint(configured),
                    true,
                    true,
                    customScope);
                continue;
            }

            var isLocalTargetOffline = CheckIfStdioLocalTargetOffline(configured.Id, out var detectedLocalScope);
            if (detectedLocalScope) customScope = "local";
            AddUpstream(
                configured.Id,
                configured.Name,
                runtime!.EndpointUrl,
                null,
                "stdio",
                runtime!.Fingerprint,
                true,
                false,
                customScope,
                isLocalTargetOffline);
        }

        configuredUpstreams = configuredUpstreams
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var payload = new RouterUpstreamsPayload { Upstreams = configuredUpstreams, ProxyEnv = ExtractProxyEnv(BuildRouterEnvironment(McpDependencyResolver.Resolve())) };
        var serializer = new DataContractJsonSerializer(
            typeof(RouterUpstreamsPayload),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        byte[] body;
        using (var memory = new MemoryStream())
        {
            serializer.WriteObject(memory, payload);
            body = memory.ToArray();
        }

        var fingerprint = Convert.ToBase64String(body);
        var fileMatches = false;
        try
        {
            if (File.Exists(AppPaths.RouterUpstreamsConfig))
                fileMatches = File.ReadAllBytes(AppPaths.RouterUpstreamsConfig).SequenceEqual(body);
        }
        catch (IOException exception)
        {
            ErrorLog.Write("McpInfrastructureService.RouterUpstreamsConfigRead", exception);
        }

        if (!fileMatches)
            WriteJsonFile(AppPaths.RouterUpstreamsConfig, payload);


        if (!_routerReady || _rootsFilter == null || IsExited(_rootsFilter.Process))
        {
            return false;
        }
        if (string.Equals(_lastPushedUpstreamsFingerprint, fingerprint, StringComparison.Ordinal))
        {
            RecordPublishedConfigurationFingerprints(configuredUpstreams);
            return false;
        }

        var request = (HttpWebRequest)WebRequest.Create(RouterUpstreamsEndpoint);
        request.Method = "POST";
        request.ContentType = "application/json";
        request.Headers["X-McpBridge-Manager-Key"] = _managerApiKey;
        request.Proxy = null;
        request.Timeout = 5000;
        request.ReadWriteTimeout = 5000;
        request.ContentLength = body.Length;
        using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
            await requestStream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
        using var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
        _lastPushedUpstreamsFingerprint = fingerprint;
        RecordPublishedConfigurationFingerprints(configuredUpstreams);
        return true;
    }

    private string? FindExistingWorkerIdForCandidate(
        CustomMcpServerEntry candidate,
        IEnumerable<string>? filesystemRoots)
    {
        if (!candidate.Enabled) return null;

        if (string.Equals(candidate.Id, SettingsStore.BuiltInUnrealId, StringComparison.OrdinalIgnoreCase))
        {
            if (_unrealDeleted || !_unrealEnabled) return null;
            var configuredUrl = _unrealCustom != null && !string.IsNullOrWhiteSpace(_unrealCustom.Url)
                ? _unrealCustom.Url!
                : UnrealEndpoint;
            if (string.Equals(candidate.Transport, "http", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    BuildHttpConfigurationFingerprint(candidate.Url ?? UnrealEndpoint, candidate.Headers),
                    BuildHttpConfigurationFingerprint(configuredUrl, _unrealCustom?.Headers),
                    StringComparison.Ordinal))
                return SettingsStore.BuiltInUnrealId;
            return null;
        }

        if (string.Equals(candidate.Id, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase))
        {
            if (_filesystemDeleted || !_filesystemEnabled)
                return null;

            var roots = NormalizeRoots(filesystemRoots ?? _activeRoots);
            if (!_activeRoots.SequenceEqual(roots, StringComparer.OrdinalIgnoreCase))
                return null;

            var filesystemCandidateFingerprint = BuildFilesystemFingerprint(candidate, roots);
            if (_filesystemGateway != null && !IsExited(_filesystemGateway.Process))
            {
                return string.Equals(filesystemCandidateFingerprint, _activeFilesystemFingerprint, StringComparison.Ordinal)
                    ? SettingsStore.BuiltInFilesystemId
                    : null;
            }

            var configuredFingerprint = BuildFilesystemFingerprint(_filesystemCustom, roots);
            return string.Equals(filesystemCandidateFingerprint, configuredFingerprint, StringComparison.Ordinal) &&
                _statusById.TryGetValue(SettingsStore.BuiltInFilesystemId, out var filesystemStatus) &&
                (filesystemStatus.HealthState == McpServerHealthState.Waiting ||
                 filesystemStatus.HealthState == McpServerHealthState.Offline)
                ? SettingsStore.BuiltInFilesystemId
                : null;
        }

        var configured = _customServers.FirstOrDefault(server =>
            string.Equals(server.Id, candidate.Id, StringComparison.OrdinalIgnoreCase));
        if (configured == null || !configured.Enabled ||
            !string.Equals(configured.Transport, candidate.Transport, StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(configured.Transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(
                BuildHttpConfigurationFingerprint(configured.Url ?? string.Empty, configured.Headers),
                BuildHttpConfigurationFingerprint(candidate.Url ?? string.Empty, candidate.Headers),
                StringComparison.Ordinal))
                return null;
            return configured.Id;
        }

        var candidateFingerprint = BuildStdioFingerprint(candidate);
        if (!string.Equals(candidateFingerprint, BuildStdioFingerprint(configured), StringComparison.Ordinal))
            return null;

        if (!_customStdioRuntimes.TryGetValue(configured.Id, out var runtime) ||
            IsExited(runtime!.Child.Process))
        {
            return _statusById.TryGetValue(configured.Id, out var pendingStatus) &&
                (pendingStatus.HealthState == McpServerHealthState.Waiting ||
                 pendingStatus.HealthState == McpServerHealthState.Offline)
                ? configured.Id
                : null;
        }

        return string.Equals(runtime!.Fingerprint, candidateFingerprint, StringComparison.Ordinal)
            ? configured.Id
            : null;
    }

    private static string CreateManagerApiKey()
    {
        var bytes = new byte[32];
        using (var random = new System.Security.Cryptography.RNGCryptoServiceProvider())
            random.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static string BuildHttpConfigurationFingerprint(string url, IDictionary<string, string>? headers)
    {
        var builder = new StringBuilder(url ?? string.Empty);
        if (headers != null)
        {
            foreach (var header in headers.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                builder.Append('|').Append(header.Key).Append('=').Append(header.Value);
        }
        return HashConfigurationFingerprint(builder.ToString());
    }

    private static string HashConfigurationFingerprint(string? value)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
    }

    private void StartPollCacheSubscription()
    {
        if (_disposed || _stopping || !IsRouterReady)
        {
            return;
        }
        if (_pollCacheSubscriptionTask != null && !_pollCacheSubscriptionTask.IsCompleted)
        {
            return;
        }

        _pollCacheCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _pollCacheCancellation = cancellation;
        _pollCacheSubscriptionTask = Task.Run(async () =>
        {
            try
            {
                await RefreshPollCacheSnapshotAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ErrorLog.Write("McpInfrastructureService.InitialPollCacheSnapshot", exception);
            }

            if (cancellation.IsCancellationRequested)
            {
                return;
            }
            await _pollManagerClient.SubscribeToStateChangesAsync(
                () => RefreshPollCacheSnapshotAsync(cancellation.Token),
                cancellation.Token).ConfigureAwait(false);
        });
    }

    private async Task RefreshPollCacheSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _stopping || !IsRouterReady)
        {
            return;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || _stopping || !IsRouterReady)
            {
                return;
            }
            var envelope = await _pollManagerClient.GetStateAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var state in envelope.Servers ?? new List<PollManagerServerState>())
            {
                if (string.IsNullOrWhiteSpace(state.ServerId)) continue;
                if (!_statusById.TryGetValue(state.ServerId, out var status))
                {
                    status = new ServerRuntimeStatus
                    {
                        Id = state.ServerId,
                        IsBuiltIn = string.Equals(state.ServerId, SettingsStore.BuiltInUnrealId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(state.ServerId, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase)
                    };
                    _statusById[state.ServerId] = status;
                }

                var previousHealthState = status.HealthState;
                status.Name = string.IsNullOrWhiteSpace(state.Name) ? state.ServerId : state.Name;
                var desiredEnabled = IsDesiredEnabled(state.ServerId, state.Enabled);
                status.IsEnabled = desiredEnabled;
                if (!desiredEnabled)
                {
                    ApplyDesiredEnabledToStatus(status, false);
                    continue;
                }
                if (string.Equals(state.Health, "disabled", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyDesiredEnabledToStatus(status, true);
                    continue;
                }
                status.ToolsCount = Math.Max(0, state.ToolsCount);
                status.ToolsPreview = state.ToolsPreview != null
                    ? string.Join(", ", state.ToolsPreview.Take(6))
                    : string.Empty;
                status.HealthState = state.Health switch
                {
                    "online" => McpServerHealthState.Online,
                    "downloading" => McpServerHealthState.Downloading,
                    "offline" => McpServerHealthState.Offline,
                    "disabled" => McpServerHealthState.Disabled,
                    "quarantine" => McpServerHealthState.Quarantine,
                    _ => McpServerHealthState.Waiting
                };

                if (string.Equals(state.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
                    status.TransportBadge = "STDIO -> HTTP";
                else if (string.IsNullOrWhiteSpace(status.TransportBadge))
                    status.TransportBadge = "HTTP / AUTO";

                switch (status.HealthState)
                {
                    case McpServerHealthState.Online:
                        if (string.Equals(state.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
                            MarkServerPackageReady(state.ServerId);
                        status.StatusText = I18n.Tr(
                            "Подключён и активен · инструментов: " + status.ToolsCount,
                            "Connected & active · tools: " + status.ToolsCount);
                        if (state.ResponseTimeMs.HasValue)
                            status.StatusText += " · " + state.ResponseTimeMs.Value + " ms";
                        break;

                    case McpServerHealthState.Downloading:
                        if (IsServerPackageReady(state.ServerId))
                        {
                            status.HealthState = McpServerHealthState.Waiting;
                            status.StatusText = I18n.Tr(
                                "Запуск и проверка MCP…",
                                "Starting & verifying MCP…");
                        }
                        else
                        {
                            status.StatusText = I18n.Tr(
                                "Скачивание и запуск пакета MCP…",
                                "Downloading & starting MCP package…");
                        }
                        break;

                    case McpServerHealthState.Offline:
                        if (string.Equals(state.Transport, "stdio", StringComparison.OrdinalIgnoreCase) && status.ToolsCount > 0)
                            MarkServerPackageReady(state.ServerId);
                        status.StatusText = state.Stale
                            ? I18n.Tr("Сервер офлайн · кэш сохранён", "Server offline · cache retained")
                            : I18n.Tr("Сервер офлайн", "Server offline");
                        break;

                    case McpServerHealthState.Disabled:
                        status.StatusText = I18n.Tr("Выключен", "Disabled");
                        break;

                    case McpServerHealthState.Quarantine:
                        status.StatusText = I18n.Tr("Карантин: ", "Quarantine: ") + (state.LastError ?? string.Empty);
                        break;

                    default:
                        if (status.DependentLocalPort > 0 && TcpPortInspector.GetListeningProcessIds(status.DependentLocalPort).Count == 0)
                        {
                            status.HealthState = McpServerHealthState.Offline;
                            status.ToolsCount = 0;
                            status.ToolsPreview = string.Empty;
                            status.StatusText = I18n.Tr("Сервер офлайн", "Server offline");
                        }
                        else if (state.Pending)
                        {
                            if (previousHealthState != McpServerHealthState.Waiting ||
                                string.IsNullOrWhiteSpace(status.StatusText))
                            {
                                status.StatusText = I18n.Tr(
                                    "Ожидание локального MCP endpoint",
                                    "Waiting for the local MCP endpoint");
                            }
                        }
                        else if (string.Equals(state.Scope, "local", StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(state.Transport, "http", StringComparison.OrdinalIgnoreCase) &&
                                 !string.IsNullOrWhiteSpace(state.LastError) &&
                                 state.LastError.IndexOf("ECONNREFUSED", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            status.StatusText = I18n.Tr(
                                "Ожидание локального MCP endpoint",
                                "Waiting for the local MCP endpoint");
                        }
                        else if (string.Equals(state.Transport, "stdio", StringComparison.OrdinalIgnoreCase) &&
                                 status.ToolsCount == 0 &&
                                 !IsServerPackageReady(state.ServerId))
                        {
                            status.HealthState = McpServerHealthState.Downloading;
                            status.StatusText = I18n.Tr(
                                "Скачивание и запуск пакета MCP…",
                                "Downloading & starting MCP package…");
                        }
                        else
                        {
                            status.StatusText = I18n.Tr(
                                "Подключение к MCP…",
                                "Connecting to MCP…");
                        }
                        break;
                }

                if (string.Equals(state.ServerId, SettingsStore.BuiltInUnrealId, StringComparison.OrdinalIgnoreCase))
                    _unrealStatus = status.StatusText;
                else if (string.Equals(state.ServerId, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase))
                    _filesystemStatus = status.StatusText;
            }

            PublishCurrentSnapshot();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StopAsync()
    {
        _stopping = true;
        _managedProcessMonitorTimer?.Dispose();
        _managedProcessMonitorTimer = null;
        _pollCacheCancellation?.Cancel();
        if (_pollCacheSubscriptionTask != null)
        {
            try { await _pollCacheSubscriptionTask.ConfigureAwait(false); }
            catch (Exception exception) { ErrorLog.Write("McpInfrastructureService.StopPollCacheSubscription", exception); }
            _pollCacheSubscriptionTask = null;
        }
        _pollCacheCancellation?.Dispose();
        _pollCacheCancellation = null;

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _stopCompleted) return;
            await StopProcessesCoreAsync(true).ConfigureAwait(false);
            _routerReady = false;
            _activeRoots = Array.Empty<string>();
            _stopCompleted = true;
            PublishCurrentSnapshot();
        }
        finally { _operationGate.Release(); }
    }

    private async Task StartFilesystemGatewayAsync(McpDependencySet dependencies, string[] roots)
    {
        if (!dependencies.HasNode || !dependencies.HasFilesystemMcp || !dependencies.HasSupergateway)
            throw new InvalidOperationException(BuildFilesystemDependencyUnavailableMessage(dependencies));

        var effectiveRoots = roots.Length == 0 ? new[] { AppPaths.EmptyFilesystemRootDirectory } : roots;
        var custom = _filesystemCustom;
        var customCommand = custom?.Command;
        var configuredCommand = string.IsNullOrWhiteSpace(customCommand) ? dependencies.NodeExecutable : customCommand!;
        var resolvedCommand = ResolveCommandExecutable(configuredCommand, custom?.WorkingDirectory, out var commandError);
        if (resolvedCommand == null)
            throw new InvalidOperationException(I18n.Tr("Команда Filesystem MCP не найдена: ", "Filesystem MCP command was not found: ") + commandError);

        ValidateFilesystemExtraArguments(custom?.Args ?? new List<string>(), effectiveRoots);
        var serverArguments = new List<string> { dependencies.FilesystemServerScript };
        if (custom?.Args != null) serverArguments.AddRange(custom.Args);
        serverArguments.AddRange(effectiveRoots);
        var effectiveCommand = PrepareStdioCommandAndArgs(dependencies, resolvedCommand, serverArguments, out var effectiveArguments);
        var enrichedEnv = BuildEnrichedEnvironment(dependencies, custom?.Env, SettingsStore.BuiltInFilesystemId);
        var gatewayConfig = BuildCustomStdioGatewayConfigJson(
            FilesystemPort,
            "filesystem",
            effectiveCommand,
            effectiveArguments,
            enrichedEnv,
            custom?.AdditionalJson);
        WriteJsonTextFile(AppPaths.FilesystemGatewayConfig, gatewayConfig);

        var customWorkingDirectory = custom?.WorkingDirectory;
        var workingDirectory = !string.IsNullOrWhiteSpace(customWorkingDirectory) && Directory.Exists(customWorkingDirectory)
            ? customWorkingDirectory!
            : AppPaths.InstallDirectory;
        EnsurePortAvailable(FilesystemPort, "Filesystem MCP");
        var fsChild = StartChild(dependencies.SupergatewayExecutable, "--config " + QuoteArgument(AppPaths.FilesystemGatewayConfig), workingDirectory, "Filesystem MCP gateway", enrichedEnv);
        var listener = await WaitForTcpListenerAsync(FilesystemPort, fsChild, GatewayStartupTimeoutMilliseconds).ConfigureAwait(false);
        if (!listener.Success)
        {
            await StopChildAsync(fsChild).ConfigureAwait(false);
            throw new InvalidOperationException("Filesystem MCP не открыл порт: " + listener.Message);
        }
        _filesystemGateway = fsChild;
    }

    private static void ValidateFilesystemExtraArguments(IEnumerable<string> arguments, IEnumerable<string> allowedRoots)
    {
        var normalizedAllowedRoots = allowedRoots
            .Select(root =>
            {
                try { return SettingsStore.NormalizePath(root); }
                catch { return string.Empty; }
            })
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .ToArray();

        foreach (var rawArgument in arguments)
        {
            if (string.IsNullOrWhiteSpace(rawArgument)) continue;
            var trimmedRaw = rawArgument.Trim();
            var argument = Environment.ExpandEnvironmentVariables(trimmedRaw.Trim('"'));
            var hasEqualsPath = false;
            var equalsIndex = argument.IndexOf('=');
            if (equalsIndex >= 0 && equalsIndex < argument.Length - 1)
            {
                argument = argument.Substring(equalsIndex + 1).Trim().Trim('"');
                hasEqualsPath = true;
            }

            if ((!hasEqualsPath && (trimmedRaw.StartsWith("-", StringComparison.Ordinal) || trimmedRaw.StartsWith("/", StringComparison.Ordinal))) ||
                (!Path.IsPathRooted(argument) && !Directory.Exists(argument))) continue;
            string normalized;
            try { normalized = SettingsStore.NormalizePath(argument); }
            catch { continue; }

            var isWithinAllowedRoot = normalizedAllowedRoots.Any(root =>
            {
                if (string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase)) return true;
                var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return normalized.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
            });
            if (!isWithinAllowedRoot)
                throw new InvalidOperationException(I18n.Tr(
                    "Дополнительный аргумент содержит путь вне разрешённых директорий Filesystem MCP: " + argument,
                    "An extra argument references a path outside the Filesystem MCP allow-list: " + argument));
        }
    }

    private async Task StopRouterForRestartAsync()
    {
        var child = _rootsFilter;
        _rootsFilter = null;
        _routerReady = false;

        if (child != null)
        {
            if (IsExited(child.Process))
            {
                child.DisposeJob();
                child.Process.Dispose();
            }
            else
            {
                await StopChildAsync(child).ConfigureAwait(false);
            }
        }

        var releaseError = await WaitForPortReleaseAsync(FilterPort, 3000).ConfigureAwait(false);
        if (releaseError != null)
            throw new InvalidOperationException(
                "Dynamic MCP Router (:8001) did not release its port before restart: " + releaseError);
    }

    private async Task StartRootsFilterAsync(McpDependencySet dependencies)
    {
        if (!dependencies.HasNode)
            throw new InvalidOperationException(BuildNodeDependencyUnavailableMessage(dependencies));

        EnsurePortAvailable(FilterPort, "Dynamic MCP Router (:8001)");
        if (!File.Exists(AppPaths.RouterUpstreamsConfig))
            WriteJsonFile(AppPaths.RouterUpstreamsConfig, new RouterUpstreamsPayload());
        var arguments = string.Join(" ", new[] { QuoteArgument(AppPaths.RootsFilterScript), "--listen", QuoteArgument("127.0.0.1"), "--port", FilterPort.ToString(), "--config", QuoteArgument(AppPaths.RouterUpstreamsConfig) });
        var enrichedEnv = BuildRouterEnvironment(dependencies);
        _lastRouterProxyFingerprint = GetRouterProxyFingerprint(enrichedEnv);
        _lastPushedUpstreamsFingerprint = null;
        _rootsFilter = StartChild(dependencies.NodeExecutable, arguments, AppPaths.InstallDirectory, "Dynamic MCP Router", enrichedEnv);
        var health = await WaitForHttpHealthAsync(RouterHealthEndpoint, _rootsFilter, 8000).ConfigureAwait(false);
        if (!health.Success) throw new InvalidOperationException("Dynamic MCP Router (:8001) не запустился: " + health.Message);
    }

    private async Task EnsureBridgeMcpGatewayAsync(McpDependencySet dependencies)
    {
        if (!dependencies.HasNode)
        {
            _gatewayStatus = BuildNodeDependencyUnavailableMessage(dependencies);
            PublishCurrentSnapshot();
            return;
        }

        if (IsBridgeGatewayReady)
        {
            return;
        }

        if (DateTime.UtcNow < _nextBridgeGatewayRetryUtc)
        {
            return;
        }

        ManagedChild? child = null;
        try
        {
            if (_bridgeGateway != null)
            {
                await StopChildAsync(_bridgeGateway).ConfigureAwait(false);
                _bridgeGateway = null;
            }

            var releaseError = await WaitForPortReleaseAsync(BridgeMcpPort, 1500).ConfigureAwait(false);
            if (releaseError != null)
                throw new InvalidOperationException("Порт 8004 не освободился после остановки предыдущего Bridge Gateway.");

            if (!File.Exists(AppPaths.BridgeMcpGatewayScript))
                throw new FileNotFoundException("Не найден скрипт Bridge MCP Gateway.", AppPaths.BridgeMcpGatewayScript);

            var arguments = string.Join(" ", new[]
            {
                QuoteArgument(AppPaths.BridgeMcpGatewayScript),
                "--listen", QuoteArgument("127.0.0.1"),
                "--port", BridgeMcpPort.ToString(),
                "--router", QuoteArgument(FilteredEndpoint)
            });
            var enrichedEnv = BuildEnrichedEnvironment(dependencies, null);
            enrichedEnv["MCPBRIDGE_MANAGER_API_KEY"] = _managerApiKey;
            child = StartChild(
                dependencies.NodeExecutable,
                arguments,
                AppPaths.InstallDirectory,
                "Bridge MCP Gateway",
                enrichedEnv);
            _bridgeGateway = child;

            var health = await WaitForHttpHealthAsync(BridgeHealthEndpoint, child, 10000).ConfigureAwait(false);
            if (!health.Success)
                throw new InvalidOperationException("Bridge MCP Gateway (:8004) не запустился: " + health.Message);

            _nextBridgeGatewayRetryUtc = DateTime.MinValue;
            _gatewayStatus = I18n.Tr("Bridge MCP Gateway (:8004) активен.", "Bridge MCP Gateway (:8004) is active.");
            ErrorLog.LogConnection(I18n.Tr(
                "Bridge MCP Gateway (:8004) запущен. Внешние инструменты: all_tools, call_tool.",
                "Bridge MCP Gateway (:8004) started. External tools: all_tools, call_tool."));
            PublishCurrentSnapshot();
        }
        catch (Exception exception)
        {
            if (child != null)
            {
                await StopChildAsync(child).ConfigureAwait(false);
                if (ReferenceEquals(_bridgeGateway, child)) _bridgeGateway = null;
            }

            _nextBridgeGatewayRetryUtc = DateTime.UtcNow.AddSeconds(15);
            _gatewayStatus = I18n.Tr("Bridge MCP Gateway недоступен: ", "Bridge MCP Gateway unavailable: ") + exception.Message;
            ErrorLog.Write("McpInfrastructureService.EnsureBridgeMcpGatewayAsync", exception);
            PublishCurrentSnapshot();
        }
    }

        private static string GetRouterProxyFingerprint(IDictionary<string, string> env)
    {
        env.TryGetValue("HTTPS_PROXY", out var httpsProxy);
        env.TryGetValue("HTTP_PROXY", out var httpProxy);
        env.TryGetValue("NO_PROXY", out var noProxy);
        return (httpsProxy ?? string.Empty) + "|" + (httpProxy ?? string.Empty) + "|" + (noProxy ?? string.Empty);
    }

        private static Dictionary<string, string> ExtractProxyEnv(IDictionary<string, string> routerEnv)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var k in new[] { "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "NO_PROXY", "no_proxy" })
        {
            if (routerEnv.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                dict[k] = v;
        }
        return dict;
    }

    private static readonly Dictionary<string, Tuple<Uri?, DateTime>> _proxyCache = new Dictionary<string, Tuple<Uri?, DateTime>>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> BuildRouterEnvironment(McpDependencySet dependencies)
    {
        var result = BuildEnrichedEnvironment(dependencies, null);
        result["MCPBRIDGE_MANAGER_API_KEY"] = _managerApiKey;
        var configuredServers = new List<CustomMcpServerEntry>();

        if (_unrealEnabled && _unrealCustom != null && !string.IsNullOrWhiteSpace(_unrealCustom.Url))
            configuredServers.Add(_unrealCustom);

        configuredServers.AddRange(_customServers.Where(server =>
            server.Enabled &&
            string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(server.Url)));

        Uri? selectedProxy = null;
        var directHosts = new List<string>();

                foreach (var server in configuredServers)
        {
            if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var targetUri) ||
                (!string.Equals(targetUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(targetUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                continue;

            Uri? effectiveProxy = null;
            var cacheKey = targetUri.Host;
            var fetchProxy = true;

            lock (_stateLock)
            {
                if (_proxyCache.TryGetValue(cacheKey, out var cached) && cached.Item2 > DateTime.UtcNow)
                {
                    effectiveProxy = cached.Item1;
                    fetchProxy = false;
                }
            }

            if (fetchProxy)
            {
                try
                {
                    var proxy = WebRequest.GetSystemWebProxy();
                    if (proxy != null && !proxy.IsBypassed(targetUri))
                    {
                        effectiveProxy = proxy.GetProxy(targetUri);
                        if (effectiveProxy == targetUri) effectiveProxy = null;
                    }
                }
                catch { effectiveProxy = null; }

                lock (_stateLock)
                {
                    _proxyCache[cacheKey] = new Tuple<Uri?, DateTime>(effectiveProxy, DateTime.UtcNow.AddSeconds(2));
                }
            }

            if (effectiveProxy == null)
            {
                directHosts.Add(targetUri.Host);
                continue;
            }

            if (selectedProxy == null)
                selectedProxy = effectiveProxy;
            else if (selectedProxy != effectiveProxy)
                directHosts.Add(targetUri.Host);
        }

        if (selectedProxy != null)
        {
            var proxyAddress = selectedProxy.AbsoluteUri;
            result["HTTP_PROXY"] = proxyAddress;
            result["http_proxy"] = proxyAddress;
            result["HTTPS_PROXY"] = proxyAddress;
            result["https_proxy"] = proxyAddress;

            var noProxyEntries = new List<string>();
            var existingNoProxy = Environment.GetEnvironmentVariable("NO_PROXY", EnvironmentVariableTarget.Process);
            var existingNoProxyLower = Environment.GetEnvironmentVariable("no_proxy", EnvironmentVariableTarget.Process);
            if (!string.IsNullOrWhiteSpace(existingNoProxy))
                noProxyEntries.AddRange(existingNoProxy!.Split(','));
            if (!string.IsNullOrWhiteSpace(existingNoProxyLower))
                noProxyEntries.AddRange(existingNoProxyLower!.Split(','));
            noProxyEntries.AddRange(directHosts);
            noProxyEntries.Add("127.0.0.1");
            noProxyEntries.Add("localhost");
            noProxyEntries.Add("::1");

                        var noProxy = string.Join(",", noProxyEntries
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            result["NO_PROXY"] = noProxy;
            result["no_proxy"] = noProxy;
        }

        return result;
    }

    private static Dictionary<string, string> BuildEnrichedEnvironment(
        McpDependencySet dependencies,
        IDictionary<string, string>? customEnv,
        string? serverId = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (customEnv != null)
            foreach (var kv in customEnv)
                if (!string.IsNullOrWhiteSpace(kv.Key)) result[kv.Key] = kv.Value ?? string.Empty;

        var extraPaths = new List<string>();
        if (Directory.Exists(AppPaths.DependenciesDirectory)) extraPaths.Add(AppPaths.DependenciesDirectory);
        var nodeDir = string.IsNullOrWhiteSpace(dependencies.NodeExecutable)
            ? null
            : Path.GetDirectoryName(dependencies.NodeExecutable);
        if (!string.IsNullOrWhiteSpace(nodeDir) && Directory.Exists(nodeDir)) extraPaths.Add(nodeDir!);
        var appDataNpm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        if (Directory.Exists(appDataNpm)) extraPaths.Add(appDataNpm);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localBin = Path.Combine(userProfile, ".local", "bin");
        if (Directory.Exists(localBin)) extraPaths.Add(localBin);
        var cargoBin = Path.Combine(userProfile, ".cargo", "bin");
        if (Directory.Exists(cargoBin)) extraPaths.Add(cargoBin);
        var wingetLinks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links");
        if (Directory.Exists(wingetLinks)) extraPaths.Add(wingetLinks);

        var currentPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Process) ?? string.Empty;
        var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
        var machinePath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? string.Empty;
        result["PATH"] = string.Join(Path.PathSeparator.ToString(), extraPaths.Concat(new[] { currentPath, userPath, machinePath }).Where(s => !string.IsNullOrWhiteSpace(s)));
        result["PYTHONUNBUFFERED"] = "1";
        result["NPM_CONFIG_PREFER_OFFLINE"] = "true";
        result["NPM_CONFIG_UPDATE_NOTIFIER"] = "false";
        result["NPM_CONFIG_FUND"] = "false";
        result["NPM_CONFIG_AUDIT"] = "false";

        if (!string.IsNullOrWhiteSpace(serverId))
        {
            var sharedDir = AppPaths.GetServerPackageDirectory("_shared");
            var sharedPythonDir = Path.Combine(sharedDir, "uv-py");
            var sharedPythonCacheDir = Path.Combine(sharedDir, "uv-pyc");
            Directory.CreateDirectory(sharedPythonDir);
            Directory.CreateDirectory(sharedPythonCacheDir);
            if (!result.ContainsKey("UV_PYTHON_INSTALL_DIR")) result["UV_PYTHON_INSTALL_DIR"] = sharedPythonDir;
            if (!result.ContainsKey("UV_PYTHON_CACHE_DIR")) result["UV_PYTHON_CACHE_DIR"] = sharedPythonCacheDir;

            if (!string.Equals(serverId, SettingsStore.BuiltInFilesystemId, StringComparison.OrdinalIgnoreCase))
            {
                var pkgDir = AppPaths.GetServerPackageDirectory(serverId!);
                Directory.CreateDirectory(pkgDir);
                if (!result.ContainsKey("NPM_CONFIG_CACHE")) result["NPM_CONFIG_CACHE"] = Path.Combine(pkgDir, "n");
                if (!result.ContainsKey("NPM_CONFIG_PREFIX")) result["NPM_CONFIG_PREFIX"] = Path.Combine(pkgDir, "np");
                if (!result.ContainsKey("NPM_CONFIG_LOGS_DIR")) result["NPM_CONFIG_LOGS_DIR"] = Path.Combine(pkgDir, "nl");
                if (!result.ContainsKey("NPM_CONFIG_TMP")) result["NPM_CONFIG_TMP"] = Path.Combine(pkgDir, "nt");
                if (!result.ContainsKey("UV_CACHE_DIR")) result["UV_CACHE_DIR"] = Path.Combine(pkgDir, "uc");
                if (!result.ContainsKey("UV_TOOL_DIR")) result["UV_TOOL_DIR"] = Path.Combine(pkgDir, "ut");
                if (!result.ContainsKey("UV_TOOL_BIN_DIR")) result["UV_TOOL_BIN_DIR"] = Path.Combine(pkgDir, "ub");
                if (!result.ContainsKey("PIP_CACHE_DIR")) result["PIP_CACHE_DIR"] = Path.Combine(pkgDir, "pc");
            }
        }
        return result;
    }

    private static McpGatewayConfig BuildStdioGatewayConfig(int port, string serverKey, string command, List<string> args, Dictionary<string, string>? env) =>
        new()
        {
            Host = "127.0.0.1", Port = port, OutputTransport = "streamableHttp", LogLevel = "info",
            ProtocolVersion = ProtocolVersion, HealthEndpoint = new[] { "/__manager/health" }, HealthCheck = "gateway",
            McpServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
            {
                [serverKey] = new McpServerConfig { Command = command, Args = args, Env = env != null && env.Count > 0 ? new Dictionary<string, string>(env, StringComparer.OrdinalIgnoreCase) : null, OutputTransport = "streamableHttp" }
            }
        };

    private static string PrepareStdioCommandAndArgs(McpDependencySet dependencies, string resolvedCommandPath, List<string> originalArgs, out List<string> effectiveArgs)
    {
        effectiveArgs = new List<string>(originalArgs);
        var fileName = Path.GetFileName(resolvedCommandPath);
        if (string.Equals(fileName, "npx.cmd", StringComparison.OrdinalIgnoreCase) || string.Equals(fileName, "npx", StringComparison.OrdinalIgnoreCase))
        {
            var cmdDir = Path.GetDirectoryName(resolvedCommandPath) ?? Path.GetDirectoryName(dependencies.NodeExecutable) ?? string.Empty;
            var npxCliJs = Path.Combine(cmdDir, "node_modules", "npm", "bin", "npx-cli.js");
            if (File.Exists(npxCliJs)) { effectiveArgs.Insert(0, npxCliJs); return dependencies.NodeExecutable; }
        }
        if (resolvedCommandPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || resolvedCommandPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var comSpec = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(comSpec) || !File.Exists(comSpec)) comSpec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            effectiveArgs.Insert(0, resolvedCommandPath);
            effectiveArgs.Insert(0, "/c");
            effectiveArgs.Insert(0, "/d");
            return comSpec;
        }
        return resolvedCommandPath;
    }

    private static string? ResolveCommandExecutable(string? rawCommand, string? workingDirectory, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(rawCommand)) { error = I18n.Tr("Команда не указана.", "Command is not specified."); return null; }
        var expanded = Environment.ExpandEnvironmentVariables(rawCommand!.Trim().Trim('"'));
        if (string.Equals(expanded, "node", StringComparison.OrdinalIgnoreCase) || string.Equals(expanded, "node.exe", StringComparison.OrdinalIgnoreCase))
            try { var deps = McpDependencyResolver.Resolve(); if (!string.IsNullOrWhiteSpace(deps.NodeExecutable) && File.Exists(deps.NodeExecutable)) return deps.NodeExecutable; } catch { }
        if (string.Equals(expanded, "npx", StringComparison.OrdinalIgnoreCase) || string.Equals(expanded, "npx.cmd", StringComparison.OrdinalIgnoreCase))
            try { var deps = McpDependencyResolver.Resolve(); var nodeDir = Path.GetDirectoryName(deps.NodeExecutable); if (!string.IsNullOrWhiteSpace(nodeDir)) { var npxCmd = Path.Combine(nodeDir!, "npx.cmd"); if (File.Exists(npxCmd)) return npxCmd; } } catch { }

        if (Path.IsPathRooted(expanded))
        {
            if (File.Exists(expanded)) return expanded;
            foreach (var ext in new[] { ".exe", ".cmd", ".bat" }) if (File.Exists(expanded + ext)) return expanded + ext;
            error = I18n.Tr("Файл не найден: ", "File not found: ") + expanded;
            return null;
        }
        var pathDirs = new List<string> { AppPaths.DependenciesDirectory };
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)) pathDirs.Add(workingDirectory!);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        pathDirs.Add(Path.Combine(userProfile, ".local", "bin"));
        pathDirs.Add(Path.Combine(userProfile, ".cargo", "bin"));
        pathDirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));
        pathDirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var p = Environment.GetEnvironmentVariable("Path", target);
            if (!string.IsNullOrWhiteSpace(p)) pathDirs.AddRange(p!.Split(Path.PathSeparator));
        }
        var extensions = Path.HasExtension(expanded) ? new[] { "" } : new[] { ".exe", ".cmd", ".bat", "" };
        foreach (var rawDir in pathDirs)
        {
            if (string.IsNullOrWhiteSpace(rawDir)) continue;
            var dir = rawDir.Trim().Trim('"');
            foreach (var ext in extensions)
                try { var full = Path.Combine(dir, expanded + ext); if (File.Exists(full)) return full; } catch { }
        }
        error = I18n.Tr("Нет зависимости: " + rawCommand, "Missing: " + rawCommand);
        return null;
    }

    private static readonly object PortAllocationLock = new();
    private static readonly HashSet<int> ReservedPortsInFlight = new();

    private static int AllocateFreePort(int startPort, int endPort)
    {
        lock (PortAllocationLock)
        {
            for (var port = startPort; port <= endPort; port++)
            {
                if (ReservedPortsInFlight.Contains(port)) continue;
                if (TcpPortInspector.TryBindLoopback(port, out _))
                {
                    ReservedPortsInFlight.Add(port);
                    return port;
                }
            }
        }
        throw new InvalidOperationException("Нет свободных портов " + startPort + "-" + endPort);
    }

    private static string BuildNodeDependencyUnavailableMessage(McpDependencySet dependencies)
    {
        return I18n.Tr("Нет зависимости: Node.js", "Missing: Node.js");
    }

    private static string BuildFilesystemDependencyUnavailableMessage(McpDependencySet dependencies)
    {
        if (!dependencies.HasNode) return I18n.Tr("Нет зависимости: Node.js", "Missing: Node.js");
        if (!dependencies.HasSupergateway) return I18n.Tr("Нет зависимости: Supergateway", "Missing: Supergateway");
        return I18n.Tr("Нет зависимости: Filesystem MCP", "Missing: Filesystem MCP");
    }

    private static string BuildFilesystemFingerprint(CustomMcpServerEntry? server, string[] roots)
    {
        var effective = server?.Clone() ?? new CustomMcpServerEntry { Id = SettingsStore.BuiltInFilesystemId, Name = "Filesystem MCP", Transport = "stdio" };
        if (string.IsNullOrWhiteSpace(effective.Command)) effective.Command = "node";
        return BuildStdioFingerprint(effective) + "|allowed-roots=" + string.Join(";", roots);
    }

    private static string BuildStdioFingerprint(CustomMcpServerEntry s)
    {
        var sb = new StringBuilder();
        sb.Append(s.Transport).Append('|').Append(s.Command).Append('|').Append(s.WorkingDirectory).Append('|').Append(string.Join(";", s.Args ?? new List<string>())).Append('|');
        if (s.Env != null)
            foreach (var kv in s.Env.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
        sb.Append('|').Append(s.AdditionalJson ?? string.Empty);
        return sb.ToString();
    }

    private static string BuildCustomStdioGatewayConfigJson(int port, string serverKey, string command, List<string> args, Dictionary<string, string>? env, string? additionalJson)
    {
        var serverConfig = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["command"] = command,
            ["args"] = args,
            ["outputTransport"] = "streamableHttp",
            ["stateful"] = true
        };
        var childEnv = env != null
            ? new Dictionary<string, string>(env, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Directory.CreateDirectory(AppPaths.CustomGatewaysDirectory);
            var safeKey = SettingsStore.SanitizeServerId(serverKey);
            var netLogPath = Path.Combine(AppPaths.CustomGatewaysDirectory, "net-" + safeKey + ".jsonl");
            childEnv["MCPBRIDGE_NET_LOG"] = netLogPath;
            childEnv["MCPBRIDGE_SELF_PORT"] = port.ToString();
            foreach (var rawArg in args)
            {
                if (string.IsNullOrWhiteSpace(rawArg)) continue;
                var trimmedArg = rawArg.Trim().Trim('"');
                if (trimmedArg.StartsWith("--user-data-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    var dirVal = Environment.ExpandEnvironmentVariables(trimmedArg.Substring("--user-data-dir=".Length).Trim().Trim('"'));
                    var dtFile = Path.Combine(dirVal, "DevToolsActivePort");
                    var dtPort = 9222;
                    if (File.Exists(dtFile))
                    {
                        try
                        {
                            var lines = File.ReadAllLines(dtFile);
                            if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out var p) && p > 0 && p <= 65535) dtPort = p;
                        }
                        catch { }
                    }
                    var escapedDtFile = dtFile.Replace("\\", "/");
                    var seedLine = "{\"ts\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ",\"host\":\"127.0.0.1\",\"port\":" + dtPort + ",\"scope\":\"local\",\"ok\":true,\"code\":\"OK\",\"portFile\":\"" + escapedDtFile + "\"}" + Environment.NewLine;
                    if (!File.Exists(netLogPath) || File.ReadAllText(netLogPath).IndexOf("\"scope\":\"local\"", StringComparison.OrdinalIgnoreCase) < 0)
                        File.AppendAllText(netLogPath, seedLine, new UTF8Encoding(false));
                }
            }

            var nodeHookPath = Path.Combine(AppPaths.AdaptersDirectory, "socket-hook.js");
            if (File.Exists(nodeHookPath))
            {
                var reqArg = "--require \"" + nodeHookPath.Replace("\\", "/") + "\"";
                childEnv["NODE_OPTIONS"] = childEnv.TryGetValue("NODE_OPTIONS", out var existingNodeOpts) && !string.IsNullOrWhiteSpace(existingNodeOpts)
                    ? existingNodeOpts + " " + reqArg
                    : reqArg;
            }

            var pyHookDir = Path.Combine(AppPaths.CustomGatewaysDirectory, "pyhook");
            Directory.CreateDirectory(pyHookDir);
            var siteCustomizePath = Path.Combine(pyHookDir, "sitecustomize.py");
            const string pyHookCode =
                "import os, socket, json, time\n" +
                "_log = os.environ.get('MCPBRIDGE_NET_LOG')\n" +
                "_self = int(os.environ.get('MCPBRIDGE_SELF_PORT') or 0)\n" +
                "def _is_local(h):\n" +
                "    h = str(h or 'localhost').lower().strip()\n" +
                "    return h in ('localhost', '127.0.0.1', '::1', '[::1]') or h.startswith('127.')\n" +
                "def _write(h, p, ok, code):\n" +
                "    if not _log or not isinstance(p, int) or p <= 0 or p == _self: return\n" +
                "    try:\n" +
                "        with open(_log, 'a', encoding='utf-8') as f:\n" +
                "            f.write(json.dumps({'ts': int(time.time()*1000), 'host': str(h), 'port': p, 'scope': 'local' if _is_local(h) else 'remote', 'ok': ok, 'code': str(code)}) + '\\n')\n" +
                "    except Exception: pass\n" +
                "if _log and not getattr(socket, '_mcpb_hooked', False):\n" +
                "    socket._mcpb_hooked = True\n" +
                "    _in_sp = False\n" +
                "    _orig_sp = getattr(socket, 'socketpair', None)\n" +
                "    if _orig_sp:\n" +
                "        def _wrap_sp(*a, **kw):\n" +
                "            global _in_sp\n" +
                "            _in_sp = True\n" +
                "            try: return _orig_sp(*a, **kw)\n" +
                "            finally: _in_sp = False\n" +
                "        socket.socketpair = _wrap_sp\n" +
                "    _orig_conn = socket.socket.connect\n" +
                "    _orig_conn_ex = socket.socket.connect_ex\n" +
                "    def _wrap_conn(self, addr):\n" +
                "        try:\n" +
                "            res = _orig_conn(self, addr)\n" +
                "            if not _in_sp and isinstance(addr, tuple) and len(addr) >= 2 and self.gettimeout() != 0: _write(addr[0], addr[1], True, 'OK')\n" +
                "            return res\n" +
                "        except Exception as e:\n" +
                "            code = getattr(e, 'winerror', None) or getattr(e, 'errno', None) or 'ECONNREFUSED'\n" +
                "            if not _in_sp and code != 10035 and isinstance(addr, tuple) and len(addr) >= 2: _write(addr[0], addr[1], False, code)\n" +
                "            raise\n" +
                "    def _wrap_conn_ex(self, addr):\n" +
                "        res = _orig_conn_ex(self, addr)\n" +
                "        if not _in_sp and res != 10035 and isinstance(addr, tuple) and len(addr) >= 2: _write(addr[0], addr[1], res == 0, 'OK' if res == 0 else res)\n" +
                "        return res\n" +
                "    socket.socket.connect = _wrap_conn\n" +
                "    socket.socket.connect_ex = _wrap_conn_ex\n";
            if (!File.Exists(siteCustomizePath) || File.ReadAllText(siteCustomizePath) != pyHookCode)
                File.WriteAllText(siteCustomizePath, pyHookCode, new UTF8Encoding(false));

            childEnv["PYTHONPATH"] = childEnv.TryGetValue("PYTHONPATH", out var existingPyPath) && !string.IsNullOrWhiteSpace(existingPyPath)
                ? pyHookDir + Path.PathSeparator + existingPyPath
                : pyHookDir;
        }
        catch { }

        if (childEnv.Count > 0)
            serverConfig["env"] = childEnv;

        if (!string.IsNullOrWhiteSpace(additionalJson) &&
            McpServerConfigParser.DeserializeJson(additionalJson!) is IDictionary<string, object?> additionalProperties)
        {
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "command", "args", "env", "url", "headers", "cwd", "workingDirectory",
                "type", "transport", "trustedExternalOrigin"
            };
            foreach (var item in additionalProperties)
                if (!reserved.Contains(item.Key))
                    serverConfig[item.Key] = item.Value;
        }

        var gatewayConfig = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["host"] = "127.0.0.1",
            ["port"] = port,
            ["outputTransport"] = "streamableHttp",
            ["stateful"] = true,
            ["logLevel"] = "info",
            ["protocolVersion"] = ProtocolVersion,
            ["healthEndpoint"] = new[] { "/__manager/health" },
            ["healthCheck"] = "gateway",
            ["mcpServers"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [serverKey] = serverConfig
            }
        };
        return McpServerConfigParser.SerializeJson(gatewayConfig);
    }

    private static readonly object JsonFileWriteLock = new();

    private static void WriteJsonTextFile(string path, string json)
    {
        lock (JsonFileWriteLock)
        {
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporaryPath, path, null);
            else File.Move(temporaryPath, path);
        }
    }

    private static void WriteGatewayConfig(string path, McpGatewayConfig config) => WriteJsonFile(path, config);

    private static void WriteJsonFile<T>(string path, T data)
    {
        lock (JsonFileWriteLock)
        {
            var serializer = new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = File.Create(temporaryPath))
            {
                serializer.WriteObject(stream, data);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporaryPath, path, null);
            else File.Move(temporaryPath, path);
        }
    }


    private ManagedChild StartChild(string executable, string arguments, string workingDirectory, string name, IDictionary<string, string>? env)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable, Arguments = arguments, WorkingDirectory = workingDirectory,
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (env != null)
            foreach (var kv in env)
                if (!string.IsNullOrWhiteSpace(kv.Key)) startInfo.EnvironmentVariables[kv.Key] = kv.Value ?? string.Empty;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var job = new WindowsJobObject(name);
        var child = new ManagedChild(process, job, name);
        void CaptureOutput(string? data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;
            var safeData = SanitizeChildOutput(data, env);
            child.AddOutput(safeData);
            if (safeData.StartsWith("[MCPBRIDGE-UPSTREAM-FAILURE]", StringComparison.Ordinal) ||
                safeData.StartsWith("[MCPBRIDGE-GATEWAY-FAILURE]", StringComparison.Ordinal) ||
                safeData.StartsWith("[MCPBRIDGE-POLL-MANAGER-FAILURE]", StringComparison.Ordinal))
                ErrorLog.WriteMessage("McpInfrastructureService.McpPipelineFailure", safeData);
        }

        process.OutputDataReceived += (_, args) => CaptureOutput(args.Data);
        process.ErrorDataReceived += (_, args) => CaptureOutput(args.Data);
        process.Exited += (_, _) =>
        {
            HandleUnexpectedExit(child);
        };
        if (!process.Start()) { child.DisposeJob(); process.Dispose(); throw new InvalidOperationException("Не удалось запустить: " + name); }
        job.Assign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return child;
    }

    private static string SafeProcessId(Process process)
    {
        try { return process.Id.ToString(); }
        catch { return "unavailable"; }
    }

    private void HandleUnexpectedExit(ManagedChild child)
    {
        if (child.ExpectedExit || _disposed) return;
        int exitCode = -1;
        try { exitCode = child.Process.ExitCode; } catch { }
        var output = GetChildOutput(child);
        ErrorLog.WriteMessage(
            "McpInfrastructureService.UnexpectedExit",
            I18n.Tr(
                "Управляемый процесс «" + child.Name + "» неожиданно завершился (код " + exitCode + ").",
                "Managed process '" + child.Name + "' exited unexpectedly (exit code " + exitCode + ").") + output);
        child.TryTerminateJob(out _);
        child.DisposeJob();
        PublishCurrentSnapshot();
    }

    private List<ServerRuntimeStatus> GetOrderedStatusList()
    {
        lock (_stateLock)
        {
            var list = new List<ServerRuntimeStatus>();
            if (_statusById.TryGetValue(SettingsStore.BuiltInUnrealId, out var unreal)) list.Add(unreal);
            if (_statusById.TryGetValue(SettingsStore.BuiltInFilesystemId, out var fs)) list.Add(fs);
            foreach (var custom in _customServers)
                if (_statusById.TryGetValue(custom.Id, out var s)) list.Add(s);
            return list;
        }
    }

        private void PublishCurrentSnapshot()
    {
        _lastPublishedGatewayReady = IsReady;
        foreach (var item in GetOrderedStatusList()) { var d = IsDesiredEnabled(item.Id, item.IsEnabled); if (item.IsEnabled != d || (!d && item.HealthState != McpServerHealthState.Disabled)) ApplyDesiredEnabledToStatus(item, d); }
        var snapshots = GetOrderedStatusList().Select(s => s.ToSnapshot()).ToList();
        var enabledSnapshots = snapshots.Where(s => s.IsEnabled).ToList();
        var onlineCount = snapshots.Count(s => s.IsEnabled && s.HealthState == McpServerHealthState.Online);
        var enabledCount = snapshots.Count(s => s.IsEnabled);
        var totalTools = snapshots.Where(s => s.IsEnabled && s.HealthState == McpServerHealthState.Online).Sum(s => s.ToolsCount);

        var fpBuilder = new StringBuilder();
        fpBuilder.Append(IsReady).Append('|').Append(I18n.CurrentLanguage).Append('|')
                 .Append(_unrealStatus).Append('|').Append(_filesystemStatus).Append('|').Append(_gatewayStatus);
        foreach (var s in snapshots)
        {
            fpBuilder.Append('|').Append(s.Id).Append(':').Append(s.IsEnabled).Append(':')
                     .Append((int)s.HealthState).Append(':').Append(s.DisplayStatusText).Append(':')
                     .Append(s.ToolsCount).Append(':').Append(s.EndpointDisplay).Append(':').Append(s.PathDisplay);
        }
        var currentFp = fpBuilder.ToString();
        if (string.Equals(_lastPublishedUiFingerprint, currentFp, StringComparison.Ordinal))
            return;
        _lastPublishedUiFingerprint = currentFp;

        StatusChanged?.Invoke(this, new McpInfrastructureStatusEventArgs(_unrealStatus, _filesystemStatus, _gatewayStatus, snapshots, enabledSnapshots, onlineCount, enabledCount, totalTools));
    }

    private static async Task<ProbeResult> WaitForTcpListenerAsync(int port, ManagedChild child, int timeoutMilliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        ProbeResult last = new(false, "TCP listener не ответил.");
        while (DateTime.UtcNow < deadline)
        {
            if (IsExited(child.Process))
            {
                var exited = new ProbeResult(false, "Процесс завершился до открытия порта." + GetChildOutput(child));
                return exited;
            }
            last = TryConnectToLoopback(port, 350);
            if (last.Success)
            {
                return last;
            }
            await Task.Delay(150).ConfigureAwait(false);
        }
        return last;
    }

    private static ProbeResult TryConnectToLoopback(int port, int timeoutMilliseconds)
    {
        using var client = new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetwork);
        try
        {
            var res = client.BeginConnect(IPAddress.Loopback, port, null, null);
            using (res.AsyncWaitHandle)
                if (!res.AsyncWaitHandle.WaitOne(timeoutMilliseconds)) { client.Close(); return new ProbeResult(false, "timeout"); }
            client.EndConnect(res);
            return new ProbeResult(true, "OK");
        }
        catch (Exception ex) { return new ProbeResult(false, ex.Message); }
    }

    private static async Task<ProbeResult> WaitForHttpHealthAsync(string endpoint, ManagedChild child, int timeoutMilliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        ProbeResult last = new(false, "HTTP endpoint не ответил.");
        while (DateTime.UtcNow < deadline)
        {
            if (IsExited(child.Process))
            {
                var exited = new ProbeResult(false, "Процесс завершился." + GetChildOutput(child));
                return exited;
            }
            last = await GetHttpStatusAsync(endpoint, 1500).ConfigureAwait(false);
            if (last.Success)
            {
                return last;
            }
            await Task.Delay(150).ConfigureAwait(false);
        }
        return last;
    }

    private static async Task<ProbeResult> GetHttpStatusAsync(string endpoint, int timeoutMilliseconds)
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "GET";
            request.Proxy = null;
            request.Timeout = timeoutMilliseconds;
            using var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
            return new ProbeResult((int)response.StatusCode >= 200 && (int)response.StatusCode < 300, "OK");
        }
        catch (Exception ex) { return new ProbeResult(false, ex.Message); }
    }

    private static void EnsurePortAvailable(int port, string component)
    {
        if (!TcpPortInspector.TryBindLoopback(port, out var error))
            throw new InvalidOperationException(component + " порт занят " + port + ": " + error);
    }

    private static async Task<string?> WaitForPortReleaseAsync(int port, int timeoutMilliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (TcpPortInspector.TryBindLoopback(port, out _)) return null;
            await Task.Delay(150).ConfigureAwait(false);
        }
        return "Порт " + port + " занят.";
    }

    private static string[] NormalizeRoots(IEnumerable<string> allowedRoots) =>
        allowedRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r =>
            {
                try { return SettingsStore.NormalizePath(r); }
                catch { return string.Empty; }
            })
            .Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string QuoteArgument(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    private static bool IsExited(Process process) { try { return process.HasExited; } catch { return true; } }

    private async Task<List<string>> StopProcessesCoreAsync(bool verifyPortRelease)
    {
        foreach (var kv in _customStdioRuntimes.ToArray()) await StopChildAsync(kv.Value.Child).ConfigureAwait(false);
        _customStdioRuntimes.Clear();
        await StopChildAsync(_bridgeGateway).ConfigureAwait(false); _bridgeGateway = null;
        await StopChildAsync(_rootsFilter).ConfigureAwait(false); _rootsFilter = null;
        await StopChildAsync(_filesystemGateway).ConfigureAwait(false); _filesystemGateway = null;
        if (verifyPortRelease)
        {
            await WaitForPortReleaseAsync(BridgeMcpPort, 3000).ConfigureAwait(false);
            await WaitForPortReleaseAsync(FilterPort, 3000).ConfigureAwait(false);
            await WaitForPortReleaseAsync(FilesystemPort, 3000).ConfigureAwait(false);
        }
        return new List<string>();
    }

    private static async Task StopChildAsync(ManagedChild? child)
    {
        if (child == null) return;
        child.ExpectedExit = true;
        try
        {
            ProcessTreeTerminator.TerminateDescendants(child.ProcessId, child.Name);
            child.TryTerminateJob(out _);
            if (!IsExited(child.Process)) await Task.Run(() => child.Process.WaitForExit(3000)).ConfigureAwait(false);
        }
        catch { }
        finally { child.DisposeJob(); child.Process.Dispose(); }
    }

    private static string GetChildOutput(ManagedChild child) => string.IsNullOrWhiteSpace(child.GetOutput()) ? string.Empty : Environment.NewLine + child.GetOutput();
    private void ThrowIfDisposed() { if (_disposed || _stopping) throw new ObjectDisposedException(nameof(McpInfrastructureService)); }
    public void Dispose() { if (_disposed) return; StopAsync().GetAwaiter().GetResult(); _disposed = true; _operationGate.Dispose(); }

    private sealed class ManagedCustomStdioRuntime
    {
        public ManagedCustomStdioRuntime(string id, int port, string endpointUrl, string fingerprint, ManagedChild child)
        { Id = id; Port = port; EndpointUrl = endpointUrl; Fingerprint = fingerprint; Child = child; }
        public string Id { get; }
        public int Port { get; }
        public string EndpointUrl { get; }
        public string Fingerprint { get; }
        public ManagedChild Child { get; }
    }

    private sealed class ServerRuntimeStatus
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string EndpointDisplay { get; set; } = string.Empty;
        public string PathDisplay { get; set; } = string.Empty;
        public string TransportBadge { get; set; } = string.Empty;
        public string UpstreamUrl { get; set; } = string.Empty;
        public bool IsBuiltIn { get; set; }
        public bool IsEnabled { get; set; } = true;
        public McpServerHealthState HealthState { get; set; } = McpServerHealthState.Disabled;
        public string StatusText { get; set; } = string.Empty;
        public int ToolsCount { get; set; }
        public string ToolsPreview { get; set; } = string.Empty;
        public string LastDiagnosticLog { get; set; } = string.Empty;
        public string LastFingerprint { get; set; } = string.Empty;
        public bool RuntimeStartFailed { get; set; }
        public int DependentLocalPort { get; set; }
        public McpServerStatusSnapshot ToSnapshot() => new()
        {
            Id = Id, Name = Name, Subtitle = Subtitle, EndpointDisplay = EndpointDisplay, PathDisplay = PathDisplay,
            TransportBadge = TransportBadge, IsBuiltIn = IsBuiltIn, IsEnabled = IsEnabled, HealthState = HealthState,
            StatusText = StatusText, ToolsCount = ToolsCount, ToolsPreview = ToolsPreview, LastDiagnosticLog = LastDiagnosticLog
        };
    }

    private sealed class ManagedChild
    {
        private readonly object _sync = new();
        private readonly Queue<string> _output = new();
        private readonly WindowsJobObject _job;
        private int _jobDisposed;
        public ManagedChild(Process process, WindowsJobObject job, string name) { Process = process; _job = job; Name = name; }
        public Process Process { get; }
        public int ProcessId => Process.Id;
        public string Name { get; }
        public volatile bool ExpectedExit;
        public bool TryTerminateJob(out string error) => _job.TryTerminateAll(out error);
        public void DisposeJob() { if (Interlocked.Exchange(ref _jobDisposed, 1) == 0) _job.Dispose(); }
        public void AddOutput(string? line) { if (!string.IsNullOrWhiteSpace(line)) lock (_sync) { _output.Enqueue(line!); while (_output.Count > 50) _output.Dequeue(); } }
        public string GetOutput() { lock (_sync) return string.Join(Environment.NewLine, _output); }
    }

    private sealed class ProbeResult { public ProbeResult(bool s, string m) { Success = s; Message = m; } public bool Success { get; } public string Message { get; } }
}