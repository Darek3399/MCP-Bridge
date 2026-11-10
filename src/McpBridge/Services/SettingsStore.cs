using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using McpBridge.Models;

namespace McpBridge.Services;

public sealed class SettingsStore
{
    public const string BuiltInUnrealId = "unreal";
    public const string BuiltInFilesystemId = "filesystem";

    public AppSettings Load()
    {
        AppPaths.EnsureDirectories();
        if (!File.Exists(AppPaths.SettingsFile))
        {
            var fresh = new AppSettings();
            NormalizeSettings(fresh);
            return fresh;
        }

        try
        {
            var serializer = new DataContractJsonSerializer(
                typeof(AppSettings),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using (var stream = File.OpenRead(AppPaths.SettingsFile))
            {
                var settings = serializer.ReadObject(stream) as AppSettings ?? new AppSettings();
                NormalizeSettings(settings);
                return settings;
            }
        }
        catch (SerializationException)
        {
            throw new InvalidDataException("Файл настроек повреждён: " + AppPaths.SettingsFile + ". Исходный файл не удалён.");
        }
    }

    public void Save(AppSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        AppPaths.EnsureDirectories();
        NormalizeSettings(settings);
        settings.SchemaVersion = 6;

        var serializer = new DataContractJsonSerializer(
            typeof(AppSettings),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        var temporaryFile = AppPaths.SettingsFile + ".new";
        using (var stream = File.Create(temporaryFile))
        {
            serializer.WriteObject(stream, settings);
            stream.Flush(true);
        }

        if (File.Exists(AppPaths.SettingsFile))
        {
            File.Replace(temporaryFile, AppPaths.SettingsFile, null);
        }
        else
        {
            File.Move(temporaryFile, AppPaths.SettingsFile);
        }
    }

    private static void NormalizeSettings(AppSettings settings)
    {
        settings.AllowedRoots = NormalizeAndDeduplicate(settings.AllowedRoots ?? new List<string>());
        var allowedSet = new HashSet<string>(settings.AllowedRoots, StringComparer.OrdinalIgnoreCase);
        settings.DisabledRoots = NormalizeAndDeduplicate(settings.DisabledRoots ?? new List<string>())
            .Where(allowedSet.Contains)
            .ToList();

        if (!settings.UnrealMcpEnabled.HasValue) settings.UnrealMcpEnabled = false;
        if (!settings.FilesystemMcpEnabled.HasValue) settings.FilesystemMcpEnabled = true;

        // Legacy online-access state is intentionally ignored. A public endpoint
        // always requires a fresh explicit user action after upgrade.
        if (settings.SchemaVersion < 5)
            settings.TailscaleFunnelEnabled = false;
        settings.SchemaVersion = 6;

        settings.CustomServers ??= new List<CustomMcpServerEntry>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BuiltInUnrealId,
            BuiltInFilesystemId
        };

        foreach (var server in settings.CustomServers)
        {
            if (server == null) continue;
            server.Id = SanitizeServerId(server.Id);
            while (string.IsNullOrWhiteSpace(server.Id) || !seenIds.Add(server.Id))
            {
                server.Id = "srv_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            }
            server.Name = string.IsNullOrWhiteSpace(server.Name) ? server.Id : server.Name.Trim();
            server.Transport = string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(server.Transport, "external", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(server.Transport, "sse", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(server.Transport, "streamableHttp", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(server.Transport, "streamable-http", StringComparison.OrdinalIgnoreCase)
                ? "http"
                : "stdio";
            server.Args ??= new List<string>();
            server.Env ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            server.Headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        settings.Presets ??= new List<McpPresetEntry>();
        if (!settings.PresetsInitialized)
        {
            EnsureBuiltInPresets(settings.Presets);
            settings.PresetsInitialized = true;
        }
    }

    private static void EnsureBuiltInPresets(List<McpPresetEntry> presets)
    {
        if (!presets.Any(p => string.Equals(p.Id, "preset_all", StringComparison.OrdinalIgnoreCase)))
        {
            presets.Insert(0, new McpPresetEntry
            {
                Id = "preset_all",
                Name = "Все серверы",
                IsBuiltIn = true,
                EnabledServerIds = new List<string> { "*" }
            });
        }

        if (!presets.Any(p => string.Equals(p.Id, "preset_ue_fs", StringComparison.OrdinalIgnoreCase)))
        {
            presets.Insert(1, new McpPresetEntry
            {
                Id = "preset_ue_fs",
                Name = "Unreal + Файлы",
                IsBuiltIn = true,
                EnabledServerIds = new List<string> { BuiltInUnrealId, BuiltInFilesystemId }
            });
        }

        if (!presets.Any(p => string.Equals(p.Id, "preset_fs_only", StringComparison.OrdinalIgnoreCase)))
        {
            presets.Insert(2, new McpPresetEntry
            {
                Id = "preset_fs_only",
                Name = "Только Файлы",
                IsBuiltIn = true,
                EnabledServerIds = new List<string> { BuiltInFilesystemId }
            });
        }
    }

    public static string SanitizeServerId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var chars = raw!.Trim().ToLowerInvariant()
            .Select(ch => (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '-' ? ch : '_')
            .ToArray();
        var cleaned = new string(chars).Trim('_', '-');
        return string.IsNullOrWhiteSpace(cleaned) ? string.Empty : cleaned;
    }

    private static List<string> NormalizeAndDeduplicate(IEnumerable<string> roots)
    {
        var result = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                var fullPath = NormalizePath(root);
                if (Directory.Exists(fullPath) && unique.Add(fullPath))
                    result.Add(fullPath);
            }
            catch
            {
                // Ignore malformed paths in settings
            }
        }
        return result;
    }

    public static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        var pathRoot = Path.GetPathRoot(fullPath) ?? string.Empty;
        return fullPath.Length > pathRoot.Length
            ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath;
    }
}