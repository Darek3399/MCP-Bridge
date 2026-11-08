using System;
using System.Diagnostics;
using System.IO;

namespace McpBridge.Services;

public sealed class EditorSession : IDisposable
{
    private readonly Process _editorProcess;
    private readonly string _processName;
    private bool _disposed;

    private EditorSession(Process editorProcess, string processName)
    {
        _editorProcess = editorProcess;
        _processName = processName;
        ProcessId = editorProcess.Id;
        _editorProcess.EnableRaisingEvents = true;
        _editorProcess.Exited += EditorProcess_Exited;
    }

    public event EventHandler? Exited;
    public event EventHandler? ProjectPathUpdated;

    public int ProcessId { get; }
    public string? ProjectPath { get; private set; }
    public string ProcessName => _processName;

    public bool HasExited
    {
        get
        {
            try
            {
                return _editorProcess.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public static EditorSession Attach(int processId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited)
                throw new InvalidOperationException("Процесс Unreal Editor уже завершился.");

            var processName = process.ProcessName;
            if (!IsUnrealEditorProcess(processName))
                throw new InvalidOperationException("PID " + processId + " принадлежит процессу " + processName + ", а не Unreal Editor.");

            var session = new EditorSession(process, processName);
            process = null;
            if (session.HasExited)
                session.RaiseExited();
            return session;
        }
        finally
        {
            process?.Dispose();
        }
    }

    public void SetProjectPath(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return;
        var fullPath = Path.GetFullPath(projectPath);
        if (!File.Exists(fullPath)) return;
        if (string.Equals(ProjectPath, fullPath, StringComparison.OrdinalIgnoreCase)) return;

        ProjectPath = fullPath;
        ProjectPathUpdated?.Invoke(this, EventArgs.Empty);
    }

    public static bool IsUnrealEditorProcess(string? processName)
    {
        if (processName is null || processName.Length == 0) return false;
        return processName.StartsWith("UnrealEditor", StringComparison.OrdinalIgnoreCase) ||
               processName.StartsWith("UE4Editor", StringComparison.OrdinalIgnoreCase);
    }

    private void EditorProcess_Exited(object? sender, EventArgs e) => RaiseExited();

    private void RaiseExited() => Exited?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _editorProcess.Exited -= EditorProcess_Exited;
        _editorProcess.Dispose();
    }
}
