using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace McpBridge.Services;

/// <summary>
/// Detects Unreal Editor windows through out-of-context WinEvents. This avoids
/// the administrator-only WMI process-start subscription and does not poll.
/// </summary>
public sealed class EditorMonitor : IDisposable
{
    private const uint EventObjectCreate = 0x8000;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const int ObjIdWindow = 0;
    private const int ChildIdSelf = 0;

    private static readonly Regex ProjectArgumentRegex = new(
        "(?i)(?:\\\"(?<quoted>[^\\\"]+\\.uproject)\\\"|(?<plain>[^\\s\\\"]+\\.uproject))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _sync = new();
    private readonly Dictionary<int, EditorSession> _sessions = new();
    private readonly HashSet<int> _pendingPids = new();
    private WinEventProc? _winEventCallback;
    private IntPtr _winEventHook;
    private bool _disposed;

    public bool IsRunning
    {
        get
        {
            lock (_sync) return _winEventHook != IntPtr.Zero;
        }
    }

    public event EventHandler<EditorProcessEventArgs>? ProcessObserved;
    public event EventHandler<EditorProcessEventArgs>? ProcessNoLongerRunning;
    public event EventHandler<EditorSessionEventArgs>? SessionStarted;
    public event EventHandler<EditorSessionEventArgs>? SessionUpdated;
    public event EventHandler<EditorSessionEventArgs>? SessionEnded;
    public event EventHandler<EditorDetectionFailedEventArgs>? DetectionFailed;

    public IReadOnlyList<EditorSession> Sessions
    {
        get
        {
            lock (_sync) return new List<EditorSession>(_sessions.Values).AsReadOnly();
        }
    }

    /// <summary>
    /// Must be called on the WPF UI thread, which owns the message loop used
    /// by SetWinEventHook to deliver out-of-context notifications.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EditorMonitor));
            if (_winEventHook != IntPtr.Zero) return;

            WinEventProc callback = OnWinEvent;
            _winEventCallback = callback;
            _winEventHook = SetWinEventHook(
                EventObjectCreate,
                EventObjectCreate,
                IntPtr.Zero,
                callback,
                0,
                0,
                WineventOutOfContext | WineventSkipOwnProcess);

            if (_winEventHook == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                _winEventCallback = null;
                throw new Win32Exception(error, "Не удалось зарегистрировать системные события создания окон (SetWinEventHook).");
            }
        }

        DiscoverAlreadyRunningEditors();
    }

    private void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr windowHandle,
        int objectId,
        int childId,
        uint eventThreadId,
        uint eventTime)
    {
        try
        {
            if (eventType != EventObjectCreate || windowHandle == IntPtr.Zero ||
                objectId != ObjIdWindow || childId != ChildIdSelf)
                return;

            GetWindowThreadProcessId(windowHandle, out var processId);
            if (processId == 0 || processId > int.MaxValue) return;

            try
            {
                using var process = Process.GetProcessById((int)processId);
                var processName = process.ProcessName;
                if (EditorSession.IsUnrealEditorProcess(processName))
                    QueueAttach((int)processId, processName);
            }
            catch (ArgumentException)
            {
                // The window or process can disappear before it is inspected.
            }
            catch (InvalidOperationException)
            {
                // Normal race while an application is exiting.
            }
            catch (Win32Exception)
            {
                // A short-lived or protected process may not expose its metadata.
            }
        }
        catch (Exception exception)
        {
            ErrorLog.Write("EditorMonitor.OnWinEvent", exception);
            DetectionFailed?.Invoke(this, new EditorDetectionFailedEventArgs(
                0,
                "WinEvent",
                "Ошибка обработки системного события создания окна: " + ErrorLog.Describe(exception)));
        }
    }

    private void DiscoverAlreadyRunningEditors()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception exception)
        {
            ErrorLog.Write("EditorMonitor.DiscoverAlreadyRunningEditors", exception);
            DetectionFailed?.Invoke(this, new EditorDetectionFailedEventArgs(
                0,
                "Process enumeration",
                "Не удалось получить список уже запущенных процессов: " + ErrorLog.Describe(exception)));
            return;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var processName = process.ProcessName;
                    if (EditorSession.IsUnrealEditorProcess(processName))
                        QueueAttach(process.Id, processName);
                }
                catch (Exception exception) when (
                    exception is ArgumentException || exception is InvalidOperationException ||
                    exception is Win32Exception || exception is NotSupportedException)
                {
                    // Other processes can exit or deny metadata access during the one-time scan.
                }
            }
        }
    }

    private void QueueAttach(int processId, string processName)
    {
        lock (_sync)
        {
            if (_disposed || _sessions.ContainsKey(processId) || !_pendingPids.Add(processId)) return;
        }

        ProcessObserved?.Invoke(this, new EditorProcessEventArgs(processId, processName));
        _ = Task.Run(() => AttachAndResolveProject(processId, processName));
    }

    private void AttachAndResolveProject(int processId, string processName)
    {
        EditorSession? session = null;
        try
        {
            session = EditorSession.Attach(processId);
            session.Exited += Session_Exited;
            session.ProjectPathUpdated += Session_ProjectPathUpdated;

            lock (_sync)
            {
                if (_disposed || _sessions.ContainsKey(processId) || session.HasExited)
                {
                    session.Exited -= Session_Exited;
                    session.ProjectPathUpdated -= Session_ProjectPathUpdated;
                    session.Dispose();
                    session = null;
                    return;
                }

                _sessions.Add(processId, session);
            }

            SessionStarted?.Invoke(this, new EditorSessionEventArgs(session));
            var attachedSession = session;
            session = null;
            _ = Task.Run(() => ResolveProjectPath(attachedSession));
        }
        catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException)
        {
            // The editor may exit between the WinEvent and Process.GetProcessById.
            session?.Dispose();
            ProcessNoLongerRunning?.Invoke(this, new EditorProcessEventArgs(processId, processName));
        }
        catch (Exception exception)
        {
            session?.Dispose();
            ErrorLog.Write("EditorMonitor.AttachAndResolveProject (PID " + processId + ")", exception);
            DetectionFailed?.Invoke(this, new EditorDetectionFailedEventArgs(
                processId,
                processName,
                ErrorLog.Describe(exception)));
        }
        finally
        {
            lock (_sync) _pendingPids.Remove(processId);
        }
    }

    private async Task ResolveProjectPath(EditorSession session)
    {
        const int maxAttempts = 40;
        try
        {
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (session.HasExited || IsDisposed()) return;

                if (TryReadProjectPath(session.ProcessId, out var projectPath))
                {
                    session.SetProjectPath(projectPath);
                    if (!string.IsNullOrWhiteSpace(session.ProjectPath)) return;
                }

                await Task.Delay(500).ConfigureAwait(false);
            }

            if (!session.HasExited && !IsDisposed() && string.IsNullOrWhiteSpace(session.ProjectPath))
            {
                const string message = "Процесс обнаружен, но путь .uproject не удалось прочитать из командной строки.";
                DetectionFailed?.Invoke(this, new EditorDetectionFailedEventArgs(
                    session.ProcessId,
                    session.ProcessName,
                    message));
            }
        }
        catch (Exception exception)
        {
            ErrorLog.Write("EditorMonitor.ResolveProjectPath (PID " + session.ProcessId + ")", exception);
            DetectionFailed?.Invoke(this, new EditorDetectionFailedEventArgs(
                session.ProcessId,
                session.ProcessName,
                "Ошибка чтения пути проекта: " + ErrorLog.Describe(exception)));
        }
    }

    private bool IsDisposed()
    {
        lock (_sync) return _disposed;
    }

    private static bool TryReadProjectPath(int processId, out string projectPath)
    {
        projectPath = string.Empty;
        var scope = new ManagementScope(@"\\.\root\CIMV2");
        scope.Connect();

        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + processId));
        using var results = searcher.Get();
        foreach (ManagementObject item in results)
        {
            using (item)
            {
                var commandLine = item["CommandLine"] as string;
                if (string.IsNullOrWhiteSpace(commandLine)) return false;

                var match = ProjectArgumentRegex.Match(commandLine);
                if (!match.Success) return false;

                var candidate = match.Groups["quoted"].Success
                    ? match.Groups["quoted"].Value
                    : match.Groups["plain"].Value;
                var fullPath = Path.GetFullPath(candidate);
                if (!File.Exists(fullPath)) return false;

                projectPath = fullPath;
                return true;
            }
        }

        return false;
    }

    private void Session_ProjectPathUpdated(object? sender, EventArgs e)
    {
        if (sender is EditorSession session)
            SessionUpdated?.Invoke(this, new EditorSessionEventArgs(session));
    }

    private void Session_Exited(object? sender, EventArgs e)
    {
        if (sender is not EditorSession session) return;

        var removed = false;
        lock (_sync)
        {
            if (_sessions.TryGetValue(session.ProcessId, out var current) && ReferenceEquals(current, session))
            {
                _sessions.Remove(session.ProcessId);
                removed = true;
            }
        }

        if (!removed) return;
        session.Exited -= Session_Exited;
        session.ProjectPathUpdated -= Session_ProjectPathUpdated;
        SessionEnded?.Invoke(this, new EditorSessionEventArgs(session));
        session.Dispose();
    }

    public void Dispose()
    {
        EditorSession[] sessions;
        IntPtr hook;
        WinEventProc? callback;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            hook = _winEventHook;
            _winEventHook = IntPtr.Zero;
            callback = _winEventCallback;
            _winEventCallback = null;
            sessions = new List<EditorSession>(_sessions.Values).ToArray();
            _sessions.Clear();
        }

        if (hook != IntPtr.Zero)
        {
            try
            {
                if (!UnhookWinEvent(hook))
                {
                    var error = Marshal.GetLastWin32Error();
                    ErrorLog.Write("EditorMonitor.Dispose.UnhookWinEvent",
                        new Win32Exception(error, "Не удалось снять системный WinEvent hook."));
                }
            }
            catch (Exception exception)
            {
                ErrorLog.Write("EditorMonitor.Dispose.UnhookWinEvent", exception);
            }
        }

        GC.KeepAlive(callback);

        foreach (var session in sessions)
        {
            session.Exited -= Session_Exited;
            session.ProjectPathUpdated -= Session_ProjectPathUpdated;
            session.Dispose();
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProc(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}

public sealed class EditorProcessEventArgs : EventArgs
{
    public EditorProcessEventArgs(int processId, string processName)
    {
        ProcessId = processId;
        ProcessName = processName;
    }

    public int ProcessId { get; }
    public string ProcessName { get; }
}

public sealed class EditorDetectionFailedEventArgs : EventArgs
{
    public EditorDetectionFailedEventArgs(int processId, string processName, string message)
    {
        ProcessId = processId;
        ProcessName = processName;
        Message = message;
    }

    public int ProcessId { get; }
    public string ProcessName { get; }
    public string Message { get; }
}

public sealed class EditorSessionEventArgs : EventArgs
{
    public EditorSessionEventArgs(EditorSession session) => Session = session;
    public EditorSession Session { get; }
}
