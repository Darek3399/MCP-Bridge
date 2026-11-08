using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;

namespace McpBridge.Services;

/// <summary>
/// Removes only known MCP Bridge child processes that still own one of
/// the reserved listener ports and whose recorded parent process is gone or its
/// PID has been reused. Unrelated Supergateway/Node processes are never killed.
/// </summary>
internal static class StaleManagedProcessCleaner
{
    private static readonly int[] CoreManagedPorts = { 8001, 8002, 8003, 8004 };

    public static void CleanupKnownOrphans()
    {
        Dictionary<int, ProcessRecord> processes;
        try
        {
            processes = ReadProcessSnapshot();
        }
        catch (Exception exception)
        {
            ErrorLog.Write("StaleManagedProcessCleaner.ReadProcessSnapshot", exception);
            return;
        }

        var candidatePorts = new List<int>(CoreManagedPorts);
        for (var p = 8010; p <= 8099; p++)
            candidatePorts.Add(p);

        var listenersByPort = new Dictionary<int, HashSet<int>>();
        try
        {
            foreach (var port in candidatePorts)
            {
                var pids = TcpPortInspector.GetListeningProcessIds(port);
                if (pids.Count > 0)
                    listenersByPort[port] = pids;
            }
        }
        catch (Exception exception)
        {
            ErrorLog.Write("StaleManagedProcessCleaner.ReadTcpListeners", exception);
            return;
        }

        var listenerPortsByPid = new Dictionary<int, List<int>>();
        foreach (var pair in listenersByPort)
        {
            foreach (var processId in pair.Value)
            {
                if (!listenerPortsByPid.TryGetValue(processId, out var ports))
                {
                    ports = new List<int>();
                    listenerPortsByPid.Add(processId, ports);
                }
                ports.Add(pair.Key);
            }
        }

        foreach (var pair in listenerPortsByPid)
        {
            if (!processes.TryGetValue(pair.Key, out var record)) continue;
            if (!IsKnownManagerChild(record)) continue;
            if (!IsOrphaned(record, processes)) continue;

            TerminateOrphan(record, pair.Value);
        }
    }

    private static Dictionary<int, ProcessRecord> ReadProcessSnapshot()
    {
        var result = new Dictionary<int, ProcessRecord>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, Name, CommandLine, CreationDate FROM Win32_Process");
        using var collection = searcher.Get();

        foreach (ManagementObject item in collection)
        {
            using (item)
            {
                var processId = Convert.ToInt32(item["ProcessId"]);
                var parentProcessId = Convert.ToInt32(item["ParentProcessId"]);
                var name = Convert.ToString(item["Name"]) ?? string.Empty;
                var commandLine = Convert.ToString(item["CommandLine"]) ?? string.Empty;
                var creationDateText = Convert.ToString(item["CreationDate"]);
                DateTime? creationDateUtc = null;

                if (!string.IsNullOrWhiteSpace(creationDateText))
                {
                    try
                    {
                        creationDateUtc = ManagementDateTimeConverter.ToDateTime(creationDateText).ToUniversalTime();
                    }
                    catch (FormatException) { }
                    catch (ArgumentOutOfRangeException) { }
                }

                result[processId] = new ProcessRecord(
                    processId, parentProcessId, name, commandLine, creationDateUtc);
            }
        }

        return result;
    }

    private static bool IsKnownManagerChild(ProcessRecord process)
    {
        if (string.Equals(process.Name, "supergateway.exe", StringComparison.OrdinalIgnoreCase) &&
            ContainsAnyPath(process.CommandLine, new[]
            {
                AppPaths.FilesystemGatewayConfig,
                AppPaths.CompositeGatewayConfig,
                AppPaths.CustomGatewaysDirectory,
                Path.Combine(Path.GetTempPath(), "ue-mcp-gateway.json")
            }))
        {
            return true;
        }

        if (!string.Equals(process.Name, "node.exe", StringComparison.OrdinalIgnoreCase) ||
            process.CommandLine.IndexOf("--port", StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        var isRouter = ContainsPath(process.CommandLine, AppPaths.RootsFilterScript) &&
                       process.CommandLine.IndexOf("8001", StringComparison.OrdinalIgnoreCase) >= 0;
        var isBridgeGateway = ContainsPath(process.CommandLine, AppPaths.BridgeMcpGatewayScript) &&
                              process.CommandLine.IndexOf("8004", StringComparison.OrdinalIgnoreCase) >= 0;
        return isRouter || isBridgeGateway;
    }

    private static bool IsOrphaned(ProcessRecord process, IReadOnlyDictionary<int, ProcessRecord> snapshot)
    {
        if (process.ParentProcessId <= 0 || !process.CreationDateUtc.HasValue) return false;

        if (snapshot.TryGetValue(process.ParentProcessId, out var parent) && parent.CreationDateUtc.HasValue)
        {
            return parent.CreationDateUtc.Value > process.CreationDateUtc.Value.AddSeconds(1);
        }

        try
        {
            using var parentProcess = Process.GetProcessById(process.ParentProcessId);
            var parentStartUtc = parentProcess.StartTime.ToUniversalTime();
            return parentStartUtc > process.CreationDateUtc.Value.AddSeconds(1);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void TerminateOrphan(ProcessRecord record, IReadOnlyCollection<int> ports)
    {
        try
        {
            using var process = Process.GetProcessById(record.ProcessId);
            var startTimeDelta = Math.Abs((process.StartTime.ToUniversalTime() - record.CreationDateUtc!.Value).TotalSeconds);
            if (startTimeDelta > 2.0)
                return;

            ProcessTreeTerminator.TerminateDescendants(record.ProcessId, "устаревший процесс Manager (до завершения root)");

            if (!process.HasExited)
            {
                process.Kill();
                if (!process.WaitForExit(5000))
                {
                    ErrorLog.WriteMessage("StaleManagedProcessCleaner", "Осиротевший процесс PID " +
                        record.ProcessId + " не завершился за 5 секунд. Порты: " + string.Join(", ", ports) + ".");
                    return;
                }
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception exception)
        {
            ErrorLog.Write("StaleManagedProcessCleaner.TerminateOrphan PID " + record.ProcessId, exception);
        }
    }

    private static bool ContainsAnyPath(string commandLine, IEnumerable<string> paths)
    {
        return paths.Any(path => ContainsPath(commandLine, path));
    }

    private static bool ContainsPath(string commandLine, string path)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            return commandLine.IndexOf(fullPath, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private sealed class ProcessRecord
    {
        public ProcessRecord(int processId, int parentProcessId, string name,
            string commandLine, DateTime? creationDateUtc)
        {
            ProcessId = processId;
            ParentProcessId = parentProcessId;
            Name = name;
            CommandLine = commandLine;
            CreationDateUtc = creationDateUtc;
        }

        public int ProcessId { get; }
        public int ParentProcessId { get; }
        public string Name { get; }
        public string CommandLine { get; }
        public DateTime? CreationDateUtc { get; }
    }
}
