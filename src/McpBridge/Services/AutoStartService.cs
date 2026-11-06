using System;
using Microsoft.Win32;
using System.Reflection;

namespace McpBridge.Services;

/// <summary>Controls optional per-user Windows startup without creating files outside the install directory.</summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MCPBridge";
    private const string LegacyValueName = "Unreal" + "MCPManager";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        key?.DeleteValue(LegacyValueName, false);
        var value = key?.GetValue(ValueName) as string;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var executablePath = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(executablePath)) return false;

        return string.Equals(NormalizeCommand(value!), NormalizePath(executablePath!), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException("Не удалось открыть раздел автозапуска Windows.");
        key.DeleteValue(LegacyValueName, false);

        if (!enabled)
        {
            key.DeleteValue(ValueName, false);
            return;
        }

        var executablePath = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("Не удалось определить путь к MCP Bridge.");

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