using System;
using Microsoft.Win32;
using System.Reflection;

namespace McpHotplug.Services;

/// <summary>Controls optional per-user Windows startup without creating files outside the install directory.</summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MCPHotplug";
    private const string LegacyBridgeValueName = "MCPBridge";
    private const string LegacyValueName = "Unreal" + "MCPManager";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        if (key == null) return false;

        key.DeleteValue(LegacyValueName, false);
        var executablePath = Assembly.GetEntryAssembly()?.Location;
        var legacyBridgeVal = key.GetValue(LegacyBridgeValueName) as string;
        if (!string.IsNullOrWhiteSpace(legacyBridgeVal))
        {
            key.DeleteValue(LegacyBridgeValueName, false);
            if (!string.IsNullOrWhiteSpace(executablePath) && string.IsNullOrWhiteSpace(key.GetValue(ValueName) as string))
            {
                key.SetValue(ValueName, "\"" + executablePath + "\"", RegistryValueKind.String);
            }
        }

        var value = key.GetValue(ValueName) as string;
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(executablePath)) return false;

        return string.Equals(NormalizeCommand(value!), NormalizePath(executablePath!), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException("Не удалось открыть раздел автозапуска Windows.");
        key.DeleteValue(LegacyValueName, false);
        key.DeleteValue(LegacyBridgeValueName, false);

        if (!enabled)
        {
            key.DeleteValue(ValueName, false);
            return;
        }

        var executablePath = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("Не удалось определить путь к MCP Hotplug.");

        key.SetValue(ValueName, "\"" + executablePath + "\"", RegistryValueKind.String);
    }

    private static string NormalizeCommand(string command)
    {
        var value = command.Trim();
        if (value.StartsWith("\"", StringComparison.Ordinal))
        {
            var closingQuote = value.IndexOf('"', 1);
            if (closingQuote > 1) value = value.Substring(1, closingQuote - 1);
        }
        else
        {
            var firstSpace = value.IndexOf(' ');
            if (firstSpace >= 0) value = value.Substring(0, firstSpace);
        }
        return NormalizePath(value);
    }

    private static string NormalizePath(string path)
    {
        return System.IO.Path.GetFullPath(path.Trim().Trim('"'));
    }
}