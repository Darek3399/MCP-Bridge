using System;
using System.IO;

namespace McpBridge.Services;

public static class AppPaths
{
    public static string InstallDirectory
    {
        get
        {
            var fullPath = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            var pathRoot = Path.GetPathRoot(fullPath) ?? string.Empty;
            return fullPath.Length > pathRoot.Length
                ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : fullPath;
        }
    }
    public static string ConfigDirectory => Path.Combine(InstallDirectory, "Config");
    public static string CustomGatewaysDirectory => Path.Combine(ConfigDirectory, "CustomGateways");
    public static string DependenciesDirectory => Path.Combine(InstallDirectory, "Dependencies");
    public static string AdaptersDirectory => Path.Combine(InstallDirectory, "Adapters");
    public static string LogsDirectory => Path.Combine(InstallDirectory, "Logs");
    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");
    public static string FilesystemGatewayConfig => Path.Combine(ConfigDirectory, "filesystem-gateway.json");
    public static string CompositeGatewayConfig => Path.Combine(ConfigDirectory, "composite-gateway.json");
    public static string RouterUpstreamsConfig => Path.Combine(ConfigDirectory, "router-upstreams.json");
    public static string EmptyFilesystemRootDirectory => Path.Combine(DependenciesDirectory, "EmptyFilesystemRoot");
    public static string ServerPackagesDirectory => Path.Combine(ConfigDirectory, "ServerPackages");
    public static string GetServerPackageDirectory(string serverId) => Path.Combine(ServerPackagesDirectory, string.IsNullOrWhiteSpace(serverId) ? "_shared" : SettingsStore.SanitizeServerId(serverId));
    public static string RootsFilterScript => Path.Combine(AdaptersDirectory, "roots-filter.js");
    public static string BridgeMcpGatewayScript => Path.Combine(AdaptersDirectory, "bridge-mcp-gateway.js");

    public static string GetCustomGatewayConfigPath(string serverId) =>
        Path.Combine(CustomGatewaysDirectory, "gateway-" + serverId + ".json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(CustomGatewaysDirectory);
        Directory.CreateDirectory(DependenciesDirectory);
        Directory.CreateDirectory(AdaptersDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(EmptyFilesystemRootDirectory);
        
    }
}