using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace McpBridge.Services;

/// <summary>
/// Terminates descendants of a managed root process after the root exits.
/// This is a one-shot snapshot taken only during shutdown, never a polling loop.
/// </summary>
internal static class ProcessTreeTerminator
{
    private const uint SnapshotProcesses = 0x00000002;
    private const uint ProcessTerminate = 0x0001;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint ErrorInvalidParameter = 87;

    public static void TerminateDescendants(int rootProcessId, string source)
    {
        if (rootProcessId <= 0) return;

        try
        {
            var descendants = GetDescendantsPostOrder(rootProcessId);
            var failures = new StringBuilder();
            foreach (var processId in descendants)
            {
                var handle = OpenProcess(ProcessTerminate | Synchronize, false, unchecked((uint)processId));
                if (handle == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorInvalidParameter)
                        failures.AppendLine("PID " + processId + ": OpenProcess failed, Win32=" + error);
                    continue;
                }

                try
                {
                    if (!TerminateProcess(handle, 1))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error != ErrorInvalidParameter)
                            failures.AppendLine("PID " + processId + ": TerminateProcess failed, Win32=" + error);
                        continue;
                    }

                    var waitResult = WaitForSingleObject(handle, 2000);
                    if (waitResult == WaitTimeout)
                        failures.AppendLine("PID " + processId + ": termination wait timed out.");
                    else if (waitResult != WaitObject0)
                    {
                        var error = Marshal.GetLastWin32Error();
                        failures.AppendLine("PID " + processId + ": wait failed, Win32=" + error);
                    }
                }
                finally
                {
                    CloseHandle(handle);
                }
            }

            if (failures.Length > 0)
                ErrorLog.WriteMessage("ProcessTreeTerminator." + source, failures.ToString().TrimEnd());
        }
        catch (Exception exception)
        {
            ErrorLog.Write("ProcessTreeTerminator." + source, exception);
        }
    }

    private static List<int> GetDescendantsPostOrder(int rootProcessId)
    {
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось получить снимок процессов Windows.");

        try
        {
            var childrenByParent = new Dictionary<int, List<int>>();
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry32)) };
            if (!Process32First(snapshot, ref entry))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось прочитать снимок процессов Windows.");

            do
            {
                var processId = unchecked((int)entry.ProcessId);
                var parentId = unchecked((int)entry.ParentProcessId);
                if (!childrenByParent.TryGetValue(parentId, out var children))
                {
                    children = new List<int>();
                    childrenByParent.Add(parentId, children);
                }
                children.Add(processId);
                entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
            }
            while (Process32Next(snapshot, ref entry));

            var ordered = new List<int>();
            var visited = new HashSet<int>();
            Visit(rootProcessId, childrenByParent, visited, ordered);
            return ordered;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static void Visit(int parentId, Dictionary<int, List<int>> childrenByParent, HashSet<int> visited, List<int> ordered)
    {
        if (!childrenByParent.TryGetValue(parentId, out var children)) return;
        foreach (var childId in children)
        {
            if (childId == parentId || !visited.Add(childId)) continue;
            Visit(childId, childrenByParent, visited, ordered);
            ordered.Add(childId);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public ProcessEntry32()
        {
            this = default;
            ExecutableFile = string.Empty;
        }

        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}