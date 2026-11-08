using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace McpBridge.Services;

/// <summary>
/// Keeps one managed process and all of its descendants in a Windows Job Object.
/// Closing the last job handle terminates the entire process tree, including when
/// the manager exits unexpectedly.
/// </summary>
internal sealed class WindowsJobObject : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private readonly object _sync = new();
    private IntPtr _handle;

    public WindowsJobObject(string name)
    {
        Name = name;
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось создать Windows Job Object для " + name + ".");

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        var size = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось включить KILL_ON_JOB_CLOSE для " + name + ".");
        }
        catch
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public string Name { get; }

    public void Assign(Process process)
    {
        if (process == null) throw new ArgumentNullException(nameof(process));

        lock (_sync)
        {
            ThrowIfClosed();
            if (!AssignProcessToJobObject(_handle, process.Handle))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error,
                    "Windows не разрешила поместить процесс " + process.Id + " в Job Object " + Name + ". " +
                    "Проверьте ограничения родительского Job Object или права процесса.");
            }
        }
    }

    public bool TryTerminateAll(out string error)
    {
        lock (_sync)
        {
            if (_handle == IntPtr.Zero)
            {
                error = string.Empty;
                return true;
            }

            if (TerminateJobObject(_handle, 1))
            {
                error = string.Empty;
                return true;
            }

            var win32Error = Marshal.GetLastWin32Error();
            error = "TerminateJobObject завершился с Win32=" + win32Error + ": " + new Win32Exception(win32Error).Message;
            return false;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_handle == IntPtr.Zero) return;
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private void ThrowIfClosed()
    {
        if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(WindowsJobObject));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int informationClass,
        IntPtr jobObjectInformation,
        uint jobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
