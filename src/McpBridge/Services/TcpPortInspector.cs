using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace McpBridge.Services;

internal static class TcpPortInspector
{
    private const int AddressFamilyInterNetwork = 2;
    private const int AddressFamilyInterNetworkV6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const uint TcpStateListen = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoData = 232;

    public static bool IsPortActivelyListening(int port, string? portFile = null)
    {
        if (!string.IsNullOrWhiteSpace(portFile))
        {
            try
            {
                if (!System.IO.File.Exists(portFile)) return false;
                var firstLine = System.IO.File.ReadAllLines(portFile!);
                if (firstLine.Length > 0 && int.TryParse(firstLine[0].Trim(), out var parsedPort) && parsedPort > 0 && parsedPort <= 65535)
                    port = parsedPort;
            }
            catch { return false; }
        }

        if (port <= 0 || port > 65535) return false;
        HashSet<int> pids;
        try { pids = GetListeningProcessIds(port); }
        catch { return false; }

        if (pids.Count == 0) return false;

        foreach (var pid in pids)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.SessionId == 0) return true;

                var procName = proc.ProcessName;
                var anyWindow = false;
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    using (p)
                    {
                        try
                        {
                            if (p.MainWindowHandle != IntPtr.Zero)
                            {
                                anyWindow = true;
                                break;
                            }
                        }
                        catch { }
                    }
                }
                if (anyWindow) return true;
            }
            catch { }
        }
        return false;
    }

    public static HashSet<int> GetListeningProcessIds(int port)
    {
        var result = new HashSet<int>();
        ReadIpv4Listeners(port, result);
        ReadIpv6Listeners(port, result);
        return result;
    }

    private static void ReadIpv4Listeners(int port, HashSet<int> result)
    {
        var buffer = AllocateTcpTable(AddressFamilyInterNetwork, out var bufferSize);
        if (buffer == IntPtr.Zero) return;

        try
        {
            var status = GetExtendedTcpTable(buffer, ref bufferSize, true,
                AddressFamilyInterNetwork, TcpTableOwnerPidListener, 0);
            if (status == ErrorNoData) return;
            if (status != 0)
                throw new Win32Exception((int)status, "Не удалось прочитать IPv4 TCP listener'ы Windows.");

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf(typeof(MibTcpRowOwnerPid));
            var rowPointer = IntPtr.Add(buffer, sizeof(uint));

            for (var index = 0; index < count; index++)
            {
                var row = (MibTcpRowOwnerPid)Marshal.PtrToStructure(
                    IntPtr.Add(rowPointer, index * rowSize), typeof(MibTcpRowOwnerPid))!;
                var localPort = unchecked((ushort)IPAddress.NetworkToHostOrder((short)(row.LocalPort & 0xFFFF)));
                if (row.State == TcpStateListen && localPort == port && row.OwningProcessId > 0)
                    result.Add(unchecked((int)row.OwningProcessId));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ReadIpv6Listeners(int port, HashSet<int> result)
    {
        var buffer = AllocateTcpTable(AddressFamilyInterNetworkV6, out var bufferSize);
        if (buffer == IntPtr.Zero) return;

        try
        {
            var status = GetExtendedTcpTable(buffer, ref bufferSize, true,
                AddressFamilyInterNetworkV6, TcpTableOwnerPidListener, 0);
            if (status == ErrorNoData) return;
            if (status != 0)
                throw new Win32Exception((int)status, "Не удалось прочитать IPv6 TCP listener'ы Windows.");

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf(typeof(MibTcp6RowOwnerPid));
            var rowPointer = IntPtr.Add(buffer, sizeof(uint));

            for (var index = 0; index < count; index++)
            {
                var row = (MibTcp6RowOwnerPid)Marshal.PtrToStructure(
                    IntPtr.Add(rowPointer, index * rowSize), typeof(MibTcp6RowOwnerPid))!;
                var localPort = unchecked((ushort)IPAddress.NetworkToHostOrder((short)(row.LocalPort & 0xFFFF)));
                if (row.State == TcpStateListen && localPort == port && row.OwningProcessId > 0)
                    result.Add(unchecked((int)row.OwningProcessId));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntPtr AllocateTcpTable(int addressFamily, out int bufferSize)
    {
        bufferSize = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true,
            addressFamily, TcpTableOwnerPidListener, 0);

        if (status == 0 || status == ErrorNoData) return IntPtr.Zero;
        if (status != ErrorInsufficientBuffer || bufferSize <= 0)
            throw new Win32Exception((int)status, "Не удалось получить таблицу TCP listener'ов Windows.");

        return Marshal.AllocHGlobal(bufferSize);
    }

    public static bool TryBindLoopback(int port, out string message)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            message = "Порт свободен.";
            return true;
        }
        catch (SocketException exception)
        {
            message = exception.SocketErrorCode + " (NativeErrorCode=" + exception.NativeErrorCode + "): " + exception.Message;
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    public static string DescribeListeners(int port)
    {
        HashSet<int> processIds;
        try
        {
            processIds = GetListeningProcessIds(port);
        }
        catch (Exception exception)
        {
            return " Не удалось определить PID владельца порта: " + exception.Message;
        }

        if (processIds.Count == 0) return " TCP-таблица не указала PID listener'а.";

        var descriptions = new List<string>();
        foreach (var processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                descriptions.Add("PID " + processId + " (" + process.ProcessName + ")");
            }
            catch (ArgumentException)
            {
                descriptions.Add("PID " + processId + " (процесс уже завершился)");
            }
            catch (InvalidOperationException)
            {
                descriptions.Add("PID " + processId + " (информация недоступна)");
            }
            catch (Win32Exception)
            {
                descriptions.Add("PID " + processId + " (доступ запрещён)");
            }
        }

        return " Listener: " + string.Join(", ", descriptions) + ".";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        public ulong LocalAddressPart1;
        public ulong LocalAddressPart2;
        public uint LocalScopeId;
        public uint LocalPort;
        public ulong RemoteAddressPart1;
        public ulong RemoteAddressPart2;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int outBufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int ipVersion,
        int tableClass,
        uint reserved);
}
