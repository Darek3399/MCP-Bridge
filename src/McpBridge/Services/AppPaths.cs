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
    public static string DependenciesDirectory => ResolveDependenciesDirectory();
    public static string AdaptersDirectory => Path.Combine(InstallDirectory, "Adapters");
    public static string LogsDirectory => Path.Combine(InstallDirectory, "Logs");
    public static string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");
    public static string FilesystemGatewayConfig => Path.Combine(ConfigDirectory, "filesystem-gateway.json");
    public static string CompositeGatewayConfig => Path.Combine(ConfigDirectory, "composite-gateway.json");
    public static string RouterUpstreamsConfig => Path.Combine(ConfigDirectory, "router-upstreams.json");
    public static string EmptyFilesystemRootDirectory => Path.Combine(DependenciesDirectory, "EmptyFilesystemRoot");
    public static string ServerPackagesDirectory => Path.Combine(ConfigDirectory, "ServerPackages");

    public static string GetServerPackageDirectory(string serverId) =>
        Path.Combine(ServerPackagesDirectory, string.IsNullOrWhiteSpace(serverId) ? "_shared" : SettingsStore.SanitizeServerId(serverId));

    public static string RootsFilterScript => Path.Combine(AdaptersDirectory, "roots-filter.js");
    public static string BridgeMcpGatewayScript => Path.Combine(AdaptersDirectory, "bridge-mcp-gateway.js");

    public static string GetCustomGatewayConfigPath(string serverId) =>
        Path.Combine(CustomGatewaysDirectory, "gateway-" + serverId + ".json");

    private static string ResolveDependenciesDirectory()
    {
        // Installed builds keep their bundled runtime files beside MCPBridge.exe.
        var installedDependencies = Path.Combine(InstallDirectory, "Dependencies");
        if (Directory.Exists(installedDependencies))
            return installedDependencies;

        // Rider/debug/publish builds live under Work\Build. Walk upward to the
        // repository root and use its one canonical local dependency directory.
        var current = new DirectoryInfo(InstallDirectory);
        while (current != null)
        {
            var workspaceDependencies = Path.Combine(current.FullName, "Dependencies");
            if (Directory.Exists(workspaceDependencies))
                return workspaceDependencies;

            current = current.Parent;
        }

        // Preserve the previous fallback when the application is launched from
        // an unusual standalone directory without a discoverable workspace.
        return installedDependencies;
    }

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
