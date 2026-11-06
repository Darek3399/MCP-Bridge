using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace McpBridge.Services;

public sealed class McpDependencyStatus
{
    public McpDependencyStatus(
        string key,
        string name,
        string minimumVersion,
        string installedVersion,
        Version? detectedVersion,
        string feature,
        string state,
        string details,
        string path,
        bool isAvailable,
        bool isCoreRequired)
    {
        Key = key;
        Name = name;
        MinimumVersion = minimumVersion;
        InstalledVersion = installedVersion;
        DetectedVersion = detectedVersion;
        Feature = feature;
        State = state;
        Details = details;
        Path = path;
        IsAvailable = isAvailable;
        IsCoreRequired = isCoreRequired;
    }

    public string Key { get; }
    public string Name { get; }
    public string MinimumVersion { get; }
    public string InstalledVersion { get; }
    public Version? DetectedVersion { get; }
    public string Feature { get; }
    public string State { get; }
    public string Details { get; }
    public string Path { get; }
    public bool IsAvailable { get; }
    public bool IsCoreRequired { get; }
}

public sealed class McpDependencyReport
{
    private readonly Dictionary<string, McpDependencyStatus> _byKey;

    public McpDependencyReport(IReadOnlyList<McpDependencyStatus> items)
    {
        Items = items;
        _byKey = items.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<McpDependencyStatus> Items { get; }
    public bool DotNetReady => Get("dotnet").IsAvailable;
    public bool CoreReady => DotNetReady && Get("node").IsAvailable;
    public bool HttpMcpReady => CoreReady;
    public bool FilesystemReady => CoreReady && Get("filesystem").IsAvailable && Get("supergateway").IsAvailable;
    public bool StdioReady => CoreReady && Get("supergateway").IsAvailable;
    public bool PythonStdioReady => StdioReady && Get("uv").IsAvailable && Get("uvx").IsAvailable;
    public bool TailscaleReady => Get("tailscale").IsAvailable;
    public bool HasUnavailableItems => Items.Any(item => !item.IsAvailable);
    public bool HasCoreIssues => Items.Any(item => item.IsCoreRequired && !item.IsAvailable);

    public McpDependencyStatus Get(string key) =>
        _byKey.TryGetValue(key, out var item)
            ? item
            : throw new KeyNotFoundException("Unknown dependency key: " + key);

    public string Summary => !CoreReady
        ? I18n.Tr(
            "Интерфейс MCP Bridge запущен, но MCP-шлюз не готов: требуется .NET Framework 4.8+ и Node.js 18.0.0+.",
            "The MCP Bridge UI is running, but the MCP gateway is unavailable: .NET Framework 4.8+ and Node.js 18.0.0+ are required.")
        : I18n.Tr(
            "Основной Bridge готов. Необязательные функции проверяются отдельно.",
            "The Bridge core is ready. Optional features are checked independently.");
}

public sealed class McpDependencySet
{
    public McpDependencySet(McpDependencyReport report)
    {
        Report = report;
        NodeExecutable = report.Get("node").IsAvailable ? report.Get("node").Path : string.Empty;
        FilesystemServerScript = report.Get("filesystem").IsAvailable ? report.Get("filesystem").Path : string.Empty;
        SupergatewayExecutable = report.Get("supergateway").IsAvailable ? report.Get("supergateway").Path : string.Empty;
        UvExecutable = report.Get("uv").IsAvailable ? report.Get("uv").Path : string.Empty;
        UvxExecutable = report.Get("uvx").IsAvailable ? report.Get("uvx").Path : string.Empty;
        TailscaleExecutable = report.Get("tailscale").IsAvailable ? report.Get("tailscale").Path : string.Empty;
        NodeVersion = report.Get("node").DetectedVersion ?? new Version(0, 0, 0);
    }

    public McpDependencyReport Report { get; }
    public string NodeExecutable { get; }
    public string FilesystemServerScript { get; }
    public string SupergatewayExecutable { get; }
    public string UvExecutable { get; }
    public string UvxExecutable { get; }
    public string TailscaleExecutable { get; }
    public Version NodeVersion { get; }
    public bool HasNode => !string.IsNullOrWhiteSpace(NodeExecutable);
    public bool HasFilesystemMcp => !string.IsNullOrWhiteSpace(FilesystemServerScript);
    public bool HasSupergateway => !string.IsNullOrWhiteSpace(SupergatewayExecutable);
    public bool HasFilesystemFeature => HasFilesystemMcp && HasSupergateway && HasNode;
    public bool HasStdioFeature => HasSupergateway && HasNode;
}

public static class McpDependencyResolver
{
    public static readonly Version MinimumNodeVersion = new(18, 0, 0);
    public static readonly Version MinimumFilesystemMcpVersion = new(1, 0, 0);
    public static readonly Version MinimumSupergatewayVersion = new(4, 2, 1);
    public static readonly Version MinimumUvVersion = new(0, 1, 0);
    public static readonly Version MinimumTailscaleVersion = new(1, 52, 0);

    private static readonly object ReportSync = new();
    private static McpDependencyReport? _cachedReport;
    private static DateTime _cachedReportAtUtc = DateTime.MinValue;
    private static readonly TimeSpan ReportCacheLifetime = TimeSpan.FromMinutes(5);

    public static McpDependencySet Resolve() => new(CheckDependencies());

    public static McpDependencyReport? GetCachedReport()
    {
        lock (ReportSync)
            return _cachedReport;
    }

    public static void InvalidateCache()
    {
        lock (ReportSync)
        {
            _cachedReport = null;
            _cachedReportAtUtc = DateTime.MinValue;
        }
    }

    public static McpDependencyReport CheckDependencies(bool forceRefresh = false)
    {
        lock (ReportSync)
        {
            if (!forceRefresh && _cachedReport != null &&
                DateTime.UtcNow - _cachedReportAtUtc < ReportCacheLifetime)
                return _cachedReport;
        }

        var items = new List<McpDependencyStatus>();

        var nodeRuntime = FindInstalledNodeRuntime();
        var nodeAvailable = !string.IsNullOrWhiteSpace(nodeRuntime.Path) &&
            nodeRuntime.Version >= MinimumNodeVersion;
        items.Add(CreateStatus(
            "node",
            "Node.js",
            MinimumNodeVersion,
            nodeRuntime.Version.Major > 0 ? nodeRuntime.Version : null,
            nodeRuntime.Path,
            I18n.Tr("Ядро Bridge, роутер, Node STDIO", "Bridge core, router, Node STDIO"),
            nodeAvailable,
            true,
            string.IsNullOrWhiteSpace(nodeRuntime.Path)
                ? I18n.Tr("Node.js не найден в реестре, PATH и известных менеджерах версий.", "Node.js was not found in the registry, PATH or known version managers.")
                : nodeRuntime.Version < MinimumNodeVersion
                    ? I18n.Tr("Обнаружена устаревшая версия Node.js.", "An outdated Node.js version was detected.")
                    : I18n.Tr("Node.js доступен для запуска MCP-компонентов.", "Node.js is available for MCP components.")));

        var filesystemScript = FindFirstExisting(new[]
        {
            Path.Combine(AppPaths.DependenciesDirectory, "FilesystemMcp", "dist", "index.js"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@modelcontextprotocol", "server-filesystem", "dist", "index.js"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm", "node_modules", "@modelcontextprotocol", "server-filesystem", "dist", "index.js")
        });
        var filesystemVersion = ReadPackageVersion(filesystemScript);
        var filesystemMinimumNodeVersion = ReadFilesystemMinimumNodeVersion(filesystemScript);
        var filesystemNodeCompatible = nodeAvailable && nodeRuntime.Version >= filesystemMinimumNodeVersion;
        var filesystemAvailable = !string.IsNullOrWhiteSpace(filesystemScript) &&
            filesystemVersion != null && filesystemVersion >= MinimumFilesystemMcpVersion &&
            filesystemNodeCompatible;
        items.Add(CreateStatus(
            "filesystem",
            "Filesystem MCP",
            MinimumFilesystemMcpVersion,
            filesystemVersion,
            filesystemScript,
            I18n.Tr("Необязательная работа с файлами", "Optional file access"),
            filesystemAvailable,
            false,
            string.IsNullOrWhiteSpace(filesystemScript)
                ? I18n.Tr("Пакет не установлен. Остальные HTTP MCP-серверы могут работать без него.", "The package is not installed. Other HTTP MCP servers can work without it.")
                : filesystemVersion == null
                    ? I18n.Tr("Не удалось прочитать версию package.json.", "Could not read the package.json version.")
                    : filesystemVersion < MinimumFilesystemMcpVersion
                        ? I18n.Tr("Версия пакета ниже минимально поддерживаемой.", "The package version is below the supported minimum.")
                        : !filesystemNodeCompatible
                            ? I18n.Tr(
                                "Для этого пакета требуется Node.js " + filesystemMinimumNodeVersion + " или новее; текущая версия ядра Bridge: " + nodeRuntime.Version + ".",
                                "This package requires Node.js " + filesystemMinimumNodeVersion + " or newer; the current Bridge runtime is " + nodeRuntime.Version + ".")
                            : I18n.Tr("Пакет Filesystem MCP доступен.", "Filesystem MCP package is available.")));

        var supergatewayPath = FindFirstExisting(new[]
        {
            Path.Combine(AppPaths.DependenciesDirectory, "supergateway.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Supergateway", "supergateway.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Supergateway", "supergateway.exe"),
            FindOnPath("supergateway.exe")
        });
        var supergatewayVersion = ReadExecutableVersion(supergatewayPath, "--version");
        var supergatewayAvailable = !string.IsNullOrWhiteSpace(supergatewayPath) &&
            supergatewayVersion != null && supergatewayVersion >= MinimumSupergatewayVersion;
        items.Add(CreateStatus(
            "supergateway",
            "Supergateway",
            MinimumSupergatewayVersion,
            supergatewayVersion,
            supergatewayPath,
            I18n.Tr("Filesystem MCP и локальные STDIO-серверы", "Filesystem MCP and local STDIO servers"),
            supergatewayAvailable,
            false,
            string.IsNullOrWhiteSpace(supergatewayPath)
                ? I18n.Tr("Исполняемый файл не найден.", "The executable was not found.")
                : supergatewayVersion == null
                    ? I18n.Tr("Не удалось определить версию. Для конфигурации Bridge требуется Supergateway 4.2.1+.", "Could not determine the version. Bridge configuration requires Supergateway 4.2.1+.")
                    : supergatewayVersion < MinimumSupergatewayVersion
                        ? I18n.Tr("Текущая версия не поддерживает используемый Bridge формат --config.", "The installed version does not support the --config format used by Bridge.")
                        : I18n.Tr("Совместимая версия Supergateway доступна.", "A compatible Supergateway version is available.")));

        var uvPath = FindFirstExisting(new[]
        {
            Path.Combine(AppPaths.DependenciesDirectory, "uv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "uv", "uv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "uv.exe"),
            FindOnPath("uv.exe")
        });
        var uvVersion = ReadExecutableVersion(uvPath, "--version");
        var uvAvailable = !string.IsNullOrWhiteSpace(uvPath) && uvVersion != null && uvVersion >= MinimumUvVersion;
        items.Add(CreateStatus(
            "uv",
            "uv",
            MinimumUvVersion,
            uvVersion,
            uvPath,
            I18n.Tr("Python MCP-серверы", "Python MCP servers"),
            uvAvailable,
            false,
            string.IsNullOrWhiteSpace(uvPath)
                ? I18n.Tr("Не установлен. Нужен только серверам, использующим uv.", "Not installed. Required only by servers that use uv.")
                : uvVersion == null || uvVersion < MinimumUvVersion
                    ? I18n.Tr("Не удалось подтвердить совместимую версию uv.", "A compatible uv version could not be confirmed.")
                    : I18n.Tr("uv доступен.", "uv is available.")));

        var uvxPath = FindFirstExisting(new[]
        {
            Path.Combine(AppPaths.DependenciesDirectory, "uvx.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "uv", "uvx.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "uvx.exe"),
            FindOnPath("uvx.exe")
        });
        var uvxVersion = ReadExecutableVersion(uvxPath, "--version");
        var uvxAvailable = !string.IsNullOrWhiteSpace(uvxPath) && uvxVersion != null && uvxVersion >= MinimumUvVersion;
        items.Add(CreateStatus(
            "uvx",
            "uvx",
            MinimumUvVersion,
            uvxVersion,
            uvxPath,
            I18n.Tr("Запуск Python MCP-пакетов", "Running Python MCP packages"),
            uvxAvailable,
            false,
            string.IsNullOrWhiteSpace(uvxPath)
                ? I18n.Tr("Не установлен. Нужен только серверам, запускаемым через uvx.", "Not installed. Required only by servers launched through uvx.")
                : uvxVersion == null || uvxVersion < MinimumUvVersion
                    ? I18n.Tr("Не удалось подтвердить совместимую версию uvx.", "A compatible uvx version could not be confirmed.")
                    : I18n.Tr("uvx доступен.", "uvx is available.")));

        var tailscalePath = FindTailscaleExecutable();
        var tailscaleVersion = ReadExecutableVersion(tailscalePath, "version");
        var tailscaleAvailable = !string.IsNullOrWhiteSpace(tailscalePath) &&
            tailscaleVersion != null && tailscaleVersion >= MinimumTailscaleVersion;
        items.Add(CreateStatus(
            "tailscale",
            "Tailscale",
            MinimumTailscaleVersion,
            tailscaleVersion,
            tailscalePath,
            I18n.Tr("Необязательный публичный HTTPS-доступ", "Optional public HTTPS access"),
            tailscaleAvailable,
            false,
            string.IsNullOrWhiteSpace(tailscalePath)
                ? I18n.Tr("Не установлен. Локальный Bridge продолжает работать без Tailscale.", "Not installed. Local Bridge works without Tailscale.")
                : tailscaleVersion == null
                    ? I18n.Tr("Не удалось определить версию клиента.", "Could not determine the client version.")
                    : tailscaleVersion < MinimumTailscaleVersion
                        ? I18n.Tr("Для используемого интерфейса Serve/Funnel требуется клиент 1.52.0+.", "The Serve/Funnel interface used by Bridge requires client 1.52.0+.")
                        : I18n.Tr("Tailscale доступен.", "Tailscale is available.")));

        var dotNetRelease = FindDotNetFrameworkRelease();
        var dotNetAvailable = dotNetRelease >= 528040;
        items.Add(new McpDependencyStatus(
            "dotnet",
            ".NET Framework",
            "4.8+",
            dotNetAvailable ? "4.8+ (Release " + dotNetRelease + ")" : "—",
            dotNetAvailable ? new Version(4, 8, 0) : null,
            I18n.Tr("Приложение MCP Bridge", "MCP Bridge application"),
            dotNetAvailable ? I18n.Tr("Доступен", "Available") : I18n.Tr("Не найден", "Missing"),
            dotNetAvailable
                ? I18n.Tr(".NET Framework 4.8 или новее обнаружен.", ".NET Framework 4.8 or newer was detected.")
                : I18n.Tr("Требуется .NET Framework 4.8+ (Release >= 528040).", ".NET Framework 4.8+ is required (Release >= 528040)."),
            string.Empty,
            dotNetAvailable,
            true));

        var report = new McpDependencyReport(items);
        lock (ReportSync)
        {
            _cachedReport = report;
            _cachedReportAtUtc = DateTime.UtcNow;
            return _cachedReport;
        }
    }

    private static McpDependencyStatus CreateStatus(
        string key,
        string name,
        Version minimumVersion,
        Version? detectedVersion,
        string? path,
        string feature,
        bool available,
        bool isCoreRequired,
        string details)
    {
        var hasPath = !string.IsNullOrWhiteSpace(path);
        var state = available
            ? I18n.Tr("Доступен", "Available")
            : !hasPath
                ? I18n.Tr("Не найден", "Missing")
                : I18n.Tr("Ниже минимума / версия неизвестна", "Below minimum / unknown version");
        return new McpDependencyStatus(
            key,
            name,
            minimumVersion.ToString(3),
            detectedVersion?.ToString(3) ?? (hasPath ? I18n.Tr("Не определена", "Unknown") : "—"),
            detectedVersion,
            feature,
            state,
            details,
            path ?? string.Empty,
            available,
            isCoreRequired);
    }

    private static Version? ReadPackageVersion(string? scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath)) return null;
        try
        {
            var packagePath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(scriptPath)!)!, "package.json");
            if (!File.Exists(packagePath)) return null;
            var json = File.ReadAllText(packagePath);
            var match = Regex.Match(json, "\"version\"\\s*:\\s*\"(?<version>\\d+(?:\\.\\d+){1,2})\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success && Version.TryParse(NormalizeVersion(match.Groups["version"].Value), out var version)
                ? version
                : null;
        }
        catch { return null; }
    }

    private static Version ReadFilesystemMinimumNodeVersion(string? scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
            return MinimumNodeVersion;

        try
        {
            var packagePath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(scriptPath)!)!, "package.json");
            if (!File.Exists(packagePath))
                return MinimumNodeVersion;

            var packageJson = File.ReadAllText(packagePath);
            var enginesMatch = Regex.Match(
                packageJson,
                "\"engines\"\\s*:\\s*\\{(?<body>[^}]*)\\}",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!enginesMatch.Success)
                return MinimumNodeVersion;

            var nodeMatch = Regex.Match(
                enginesMatch.Groups["body"].Value,
                "\"node\"\\s*:\\s*\"(?<range>[^\"]+)\"",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!nodeMatch.Success)
                return MinimumNodeVersion;

            var minimumMatch = Regex.Match(
                nodeMatch.Groups["range"].Value,
                ">=\\s*(?<version>\\d+(?:\\.\\d+){0,2})",
                RegexOptions.CultureInvariant);
            if (!minimumMatch.Success ||
                !Version.TryParse(NormalizeVersion(minimumMatch.Groups["version"].Value), out var minimum))
                return MinimumNodeVersion;

            return minimum > MinimumNodeVersion ? minimum : MinimumNodeVersion;
        }
        catch
        {
            return MinimumNodeVersion;
        }
    }

    private static Version? ReadExecutableVersion(string? executablePath, string arguments)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return null;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            if (!process.Start()) return null;
            if (!process.WaitForExit(1800))
            {
                try { process.Kill(); } catch { }
                return null;
            }
            var output = process.StandardOutput.ReadToEnd() + " " + process.StandardError.ReadToEnd();
            var match = Regex.Match(output, @"(?<!\d)v?(?<version>\d+\.\d+(?:\.\d+)?)(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success && Version.TryParse(NormalizeVersion(match.Groups["version"].Value), out var version)
                ? version
                : null;
        }
        catch { return null; }
    }

    private static int FindDotNetFrameworkRelease()
    {
        var release = 0;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
                if (key?.GetValue("Release") is int value && value > release)
                    release = value;
            }
            catch { }
        }
        return release;
    }

    private static string? FindTailscaleExecutable()
    {
        var candidates = new List<string?>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tailscale", "tailscale.exe"),
            FindOnPath("tailscale.exe")
        };

        try
        {
            using var serviceKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tailscale");
            var imagePath = serviceKey?.GetValue("ImagePath") as string;
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                var match = Regex.Match(imagePath, "^\\s*\\\"(?<path>[^\\\"]+tailscaled\\.exe)\\\"|^\\s*(?<path>.+?tailscaled\\.exe)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var directory = Path.GetDirectoryName(match.Groups["path"].Value);
                    if (!string.IsNullOrWhiteSpace(directory))
                        candidates.Insert(0, Path.Combine(directory, "tailscale.exe"));
                }
            }
        }
        catch { }

        return FindFirstExisting(candidates);
    }

    private sealed class NodeRuntimeInfo
    {
        public NodeRuntimeInfo(string path, Version version)
        {
            Path = path;
            Version = version;
        }
        public string Path { get; }
        public Version Version { get; }
    }

    private static NodeRuntimeInfo FindInstalledNodeRuntime()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddNodePath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try
            {
                var path = Environment.ExpandEnvironmentVariables(value!.Trim().Trim('"'));
                var comma = path.IndexOf(",", StringComparison.Ordinal);
                if (comma > 0 && path.Substring(0, comma).TrimEnd().EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    path = path.Substring(0, comma).Trim().Trim('"');

                if (string.Equals(System.IO.Path.GetFileName(path), "node.exe", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(path);
                else
                    candidates.Add(System.IO.Path.Combine(path, "node.exe"));
            }
            catch { }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddNodePath(System.IO.Path.Combine(programFiles, "nodejs"));
        AddNodePath(System.IO.Path.Combine(programFilesX86, "nodejs"));
        AddNodePath(System.IO.Path.Combine(localAppData, "Programs", "nodejs"));
        AddNodePath(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Programs", "nodejs"));
        AddNodePath(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin"));
        AddNodePath(System.IO.Path.Combine(localAppData, "Volta", "bin"));
        AddNodePath(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "nodejs", "current"));
        AddNodePath(Environment.GetEnvironmentVariable("NODE_HOME"));
        AddNodePath(Environment.GetEnvironmentVariable("NODEJS_HOME"));

        foreach (var variableName in new[] { "NVM_SYMLINK", "VOLTA_HOME", "SCOOP", "ChocolateyInstall" })
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (variableName == "SCOOP")
                AddNodePath(System.IO.Path.Combine(value, "apps", "nodejs", "current"));
            else if (variableName == "ChocolateyInstall")
                AddNodePath(System.IO.Path.Combine(value, "bin"));
            else if (variableName == "VOLTA_HOME")
                AddNodePath(System.IO.Path.Combine(value, "bin"));
            else
                AddNodePath(value);
        }

        var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME");
        if (!string.IsNullOrWhiteSpace(nvmHome) && Directory.Exists(nvmHome))
        {
            try
            {
                foreach (var versionDirectory in Directory.GetDirectories(nvmHome, "v*"))
                    AddNodePath(versionDirectory);
            }
            catch { }
        }
        var fnmDir = Environment.GetEnvironmentVariable("FNM_DIR");
        if (!string.IsNullOrWhiteSpace(fnmDir))
        {
            var versionsRoot = System.IO.Path.Combine(fnmDir, "node-versions");
            try
            {
                if (Directory.Exists(versionsRoot))
                {
                    foreach (var versionDirectory in Directory.GetDirectories(versionsRoot))
                        AddNodePath(System.IO.Path.Combine(versionDirectory, "installation"));
                }
            }
            catch { }
        }

        var nvsHome = Environment.GetEnvironmentVariable("NVS_HOME");
        if (!string.IsNullOrWhiteSpace(nvsHome))
        {
            var nodeRoot = System.IO.Path.Combine(nvsHome, "node");
            try
            {
                if (Directory.Exists(nodeRoot))
                {
                    foreach (var versionDirectory in Directory.GetDirectories(nodeRoot))
                    {
                        AddNodePath(versionDirectory);
                        foreach (var architectureDirectory in Directory.GetDirectories(versionDirectory))
                            AddNodePath(System.IO.Path.Combine(architectureDirectory, "bin"));
                    }
                }
            }
            catch { }
        }

        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var pathValue = Environment.GetEnvironmentVariable("Path", target) ?? string.Empty;
            foreach (var directory in pathValue.Split(System.IO.Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(directory)) AddNodePath(directory.Trim().Trim('"'));
            }
        }

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    foreach (var subPath in new[]
                    {
                        @"SOFTWARE\Node.js",
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\node.exe"
                    })
                    {
                        try
                        {
                            using var key = baseKey.OpenSubKey(subPath);
                            if (key == null) continue;
                            foreach (var valueName in new[] { "", "InstallPath", "InstallLocation", "Path" })
                                AddNodePath(key.GetValue(valueName) as string);
                        }
                        catch { }
                    }

                    try
                    {
                        using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                        if (uninstall != null)
                        {
                            foreach (var subKeyName in uninstall.GetSubKeyNames())
                            {
                                try
                                {
                                    using var entry = uninstall.OpenSubKey(subKeyName);
                                    if (entry == null) continue;
                                    var displayName = entry.GetValue("DisplayName") as string;
                                    if (string.IsNullOrWhiteSpace(displayName) ||
                                        displayName!.IndexOf("Node.js", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    AddNodePath(entry.GetValue("InstallLocation") as string);
                                    AddNodePath(entry.GetValue("DisplayIcon") as string);
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
                catch { }
            }
        }

        var executableFromPath = FindOnPath("node.exe");
        if (!string.IsNullOrWhiteSpace(executableFromPath)) AddNodePath(executableFromPath);

        var found = new List<NodeRuntimeInfo>();
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var version = ReadNodeVersion(candidate);
                found.Add(new NodeRuntimeInfo(candidate, version));
            }
            catch { }
        }

        var best = found.OrderByDescending(item => item.Version).FirstOrDefault();
        return best ?? new NodeRuntimeInfo(string.Empty, new Version(0, 0, 0));
    }

    private static Version ReadNodeVersion(string nodeExecutable)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = nodeExecutable,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить Node.js для проверки версии.");
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(); } catch { }
            throw new TimeoutException("Проверка версии Node.js превысила 5 секунд.");
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        var match = Regex.Match(output + " " + error, @"v(?<version>\d+(?:\.\d+){1,2})", RegexOptions.CultureInvariant);
        if (!match.Success || !Version.TryParse(NormalizeVersion(match.Groups["version"].Value), out var version))
            throw new InvalidOperationException("Не удалось определить версию Node.js. Вывод: " + (output + " " + error).Trim());

        return version;
    }

    private static string NormalizeVersion(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 1 ? value + ".0.0" : parts.Length == 2 ? value + ".0" : value;
    }

    private static string? FindFirstExisting(IEnumerable<string?> candidates)
    {
        return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
    }

    private static string? FindOnPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), executableName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
            catch (PathTooLongException) { }
        }
        return null;
    }

}