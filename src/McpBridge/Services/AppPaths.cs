using System;
using System.IO;
using Microsoft.Win32;

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
    public static string UpdatesDirectory => Path.Combine(InstallDirectory, "Updates");
    public static string UpdaterExecutable => Path.Combine(InstallDirectory, "MCPBridge.Updater.exe");
    public static bool IsInstalledBuild
    {
        get
        {
            try
            {
                return Directory.Exists(InstallDirectory) &&
                       Directory.GetFiles(InstallDirectory, "unins*.exe", SearchOption.TopDirectoryOnly).Length > 0;
            }
            catch { return false; }
        }
    }

    // A Publish build is a test runner, not an installation. When it is launched
    // on a machine that already has MCP Bridge installed, updates must target that
    // registered installation and relaunch it after setup completes.
    public static string? RegisteredInstallDirectory => FindRegisteredInstallDirectory();

    public static string UpdateTargetDirectory =>
        IsInstalledBuild ? InstallDirectory : RegisteredInstallDirectory ?? string.Empty;

    public static bool CanInstallUpdates => !string.IsNullOrWhiteSpace(UpdateTargetDirectory);

    private static string? FindRegisteredInstallDirectory()
    {
        const string uninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var subKeys = new[]
        {
            @"{17473ED1-A877-4A88-8485-A68A5DD8F646}_is1",
            @"{D2AF33D2-E4E3-4AC7-94F6-7F1CC9EE2D42}_is1"
        };

        try
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var subKeyName in subKeys)
            {
                using var uninstallKey = registry.OpenSubKey(uninstallRoot + @"\" + subKeyName);
                if (uninstallKey == null)
                    continue;

                var candidate = uninstallKey.GetValue("Inno Setup: App Path") as string;
                if (string.IsNullOrWhiteSpace(candidate))
                    candidate = uninstallKey.GetValue("InstallLocation") as string;

                if (string.IsNullOrWhiteSpace(candidate))
                {
                    var uninstallString = uninstallKey.GetValue("UninstallString") as string;
                    if (!string.IsNullOrWhiteSpace(uninstallString))
                    {
                        var quoted = System.Text.RegularExpressions.Regex.Match(uninstallString, "^\\s*\"([^\"]+\\.exe)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        var executable = quoted.Success
                            ? quoted.Groups[1].Value
                            : uninstallString.Trim().Trim('"');
                        if (File.Exists(executable))
                            candidate = Path.GetDirectoryName(executable);
                    }
                }

                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                var fullPath = Path.GetFullPath(candidate.Trim().Trim('"'));
                if (Directory.Exists(fullPath) && File.Exists(Path.Combine(fullPath, "MCPBridge.exe")))
                    return fullPath;
            }
        }
        catch
        {
            // An unreadable or absent uninstall key means no registered installation.
        }

        return null;
    }
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
        Directory.CreateDirectory(UpdatesDirectory);
        Directory.CreateDirectory(EmptyFilesystemRootDirectory);
    }
}
