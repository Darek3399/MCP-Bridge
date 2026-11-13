using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace McpBridge.Updater;

internal static class Program
{
    private const int ParentExitTimeoutMilliseconds = 120_000;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
                return 2;

            if (string.Equals(args[0], "--start", StringComparison.OrdinalIgnoreCase))
                return StartWorker(args);

            if (string.Equals(args[0], "--worker", StringComparison.OrdinalIgnoreCase))
                return RunWorker(args);

            return 2;
        }
        catch (Exception exception)
        {
            TryWriteError(args, exception);
            return 1;
        }
    }

    private static int StartWorker(string[] args)
    {
        if (args.Length != 7)
            return 2;

        var installerPath = Path.GetFullPath(args[1]);
        var sourceDirectory = Path.GetFullPath(args[2]);
        var targetDirectory = Path.GetFullPath(args[3]);
        if (!int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var parentPid))
            return 2;

        var language = NormalizeLanguage(args[5]);
        var expectedSha256 = args[6];
        if (!IsSha256(expectedSha256))
            return 2;
        var updatesDirectory = Path.Combine(sourceDirectory, "Updates");
        if (!Directory.Exists(updatesDirectory) || !File.Exists(installerPath))
            throw new FileNotFoundException("The verified update installer or Updates directory is missing.", installerPath);

        var workerPath = Path.Combine(
            updatesDirectory,
            "MCPBridge.Updater.Worker." + Guid.NewGuid().ToString("N") + ".exe");

        // Inno Setup removes files owned by the previous installation during upgrade.
        // The detached worker is a private copy and is not in the old installer's file list.
        try
        {
            File.Copy(Assembly.GetExecutingAssembly().Location, workerPath, false);

            var workerArguments = JoinArguments(new[]
            {
                "--worker",
                installerPath,
                sourceDirectory,
                targetDirectory,
                parentPid.ToString(CultureInfo.InvariantCulture),
                language,
                expectedSha256,
                workerPath
            });

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = workerPath,
                Arguments = workerArguments,
                WorkingDirectory = updatesDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process == null)
                throw new InvalidOperationException("Windows did not start the background updater worker.");

            return 0;
        }
        catch (Exception exception)
        {
            TryDeleteFile(workerPath);
            WriteError(sourceDirectory, exception);
            if (WaitForParentExit(parentPid, ParentExitTimeoutMilliseconds))
                LaunchBridge(Path.Combine(sourceDirectory, "MCPBridge.exe"), workerPath, false);
            return 1;
        }
    }

    private static int RunWorker(string[] args)
    {
        using (var updateMutex = new Mutex(true, @"Local\MCPBridge.Update", out var createdNew))
        {
            if (!createdNew)
                return 0;

            try
            {
                return RunWorkerCore(args);
            }
            finally
            {
                try { updateMutex.ReleaseMutex(); } catch { }
            }
        }
    }

    private static int RunWorkerCore(string[] args)
    {
        if (args.Length != 8)
            return 2;

        var installerPath = Path.GetFullPath(args[1]);
        var sourceDirectory = Path.GetFullPath(args[2]);
        var targetDirectory = Path.GetFullPath(args[3]);
        if (!int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var parentPid))
            return 2;

        var language = NormalizeLanguage(args[5]);
        var expectedSha256 = args[6];
        var workerPath = Path.GetFullPath(args[7]);
        var sourceBridgePath = Path.Combine(sourceDirectory, "MCPBridge.exe");
        var targetBridgePath = Path.Combine(targetDirectory, "MCPBridge.exe");

        try
        {
            if (!WaitForParentExit(parentPid, ParentExitTimeoutMilliseconds))
                throw new TimeoutException("MCP Bridge did not finish its shutdown; installation was not started.");

            if (!File.Exists(installerPath))
                throw new FileNotFoundException("The downloaded update installer no longer exists.", installerPath);

            var actualSha256 = ComputeSha256(installerPath);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update installer failed SHA-256 verification immediately before execution.");

            var setup = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LANG=" + language,
                WorkingDirectory = sourceDirectory,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (setup == null)
                throw new InvalidOperationException("Windows did not start the update installer.");

            setup.WaitForExit();
            if (setup.ExitCode != 0)
                throw new InvalidOperationException("The update installer exited with code " + setup.ExitCode.ToString(CultureInfo.InvariantCulture) + ".");

            // The installer has exited successfully. It is now safe to remove
            // the downloaded payload from the temporary Publish directory.
            TryDeleteFile(installerPath);
            LaunchBridge(targetBridgePath, workerPath, true);
            return 0;
        }
        catch (Exception exception)
        {
            WriteError(sourceDirectory, exception);
            // On failure, return to the test runner that initiated the update.
            // Keep the verified installer so the next attempt can reuse it.
            LaunchBridge(sourceBridgePath, workerPath, false);
            return 1;
        }
    }

    private static bool WaitForParentExit(int processId, int timeoutMilliseconds)
    {
        try
        {
            using (var parent = Process.GetProcessById(processId))
            {
                if (parent.HasExited)
                    return true;

                return parent.WaitForExit(timeoutMilliseconds) && parent.HasExited;
            }
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void LaunchBridge(string bridgePath, string workerPath, bool removeInstallers)
    {
        if (!File.Exists(bridgePath))
        {
            WriteError(Path.GetDirectoryName(bridgePath) ?? string.Empty,
                new FileNotFoundException("MCPBridge.exe was not found after the update.", bridgePath));
            return;
        }

        var arguments = JoinArguments(new[]
        {
            "--update-handoff",
            Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
            workerPath,
            removeInstallers ? "1" : "0",
            "--skip-update-check"
        });

        Process.Start(new ProcessStartInfo
        {
            FileName = bridgePath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(bridgePath) ?? Environment.CurrentDirectory,
            UseShellExecute = false
        });
    }

    private static string NormalizeLanguage(string value) =>
        string.Equals(value, "en", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "english", StringComparison.OrdinalIgnoreCase)
            ? "english"
            : "russian";

    private static bool IsSha256(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string ComputeSha256(string path)
    {
        using (var sha256 = SHA256.Create())
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
            return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty);
    }

    private static string JoinArguments(string[] values) =>
        string.Join(" ", values.Select(QuoteArgument));

    // Windows command-line quoting that also handles trailing backslashes.
    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder();
        result.Append('"');
        var backslashes = 0;

        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }

        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    private static void TryWriteError(string[] args, Exception exception)
    {
        try
        {
            var sourceDirectory = args.Length > 2 && (
                string.Equals(args[0], "--worker", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[0], "--start", StringComparison.OrdinalIgnoreCase))
                ? Path.GetFullPath(args[2])
                : string.Empty;
            WriteError(sourceDirectory, exception);
        }
        catch
        {
            // The updater is intentionally silent; diagnostics must never create a UI.
        }
    }

    private static void WriteError(string installDirectory, Exception exception)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installDirectory))
                return;

            var logsDirectory = Path.Combine(installDirectory, "Logs");
            Directory.CreateDirectory(logsDirectory);
            File.AppendAllText(
                Path.Combine(logsDirectory, "update-error.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                " " + exception + Environment.NewLine + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch
        {
            // Logging is best-effort and must not make the updater visible.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
