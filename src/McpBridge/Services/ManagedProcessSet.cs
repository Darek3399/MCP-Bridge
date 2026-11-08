using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace McpBridge.Services;

/// <summary>Owns child processes started by the manager and shuts them down in reverse start order.</summary>
public sealed class ManagedProcessSet : IDisposable
{
    private sealed class Entry
    {
        public Entry(Process process, Action<Process>? gracefulStop)
        {
            Process = process;
            GracefulStop = gracefulStop;
        }

        public Process Process { get; }
        public Action<Process>? GracefulStop { get; }
    }

    private readonly object _sync = new();
    private readonly List<Entry> _entries = new();
    private bool _stopping;

    public void Register(Process process, Action<Process>? gracefulStop = null)
    {
        if (process == null) throw new ArgumentNullException(nameof(process));

        var stopImmediately = false;
        lock (_sync)
        {
            if (_stopping)
            {
                stopImmediately = true;
            }
            else
            {
                _entries.Add(new Entry(process, gracefulStop));
            }
        }

        if (stopImmediately)
        {
            StopEntry(new Entry(process, gracefulStop));
        }
    }

    public void StopAll()
    {
        Entry[] entries;
        lock (_sync)
        {
            if (_stopping) return;
            _stopping = true;
            entries = _entries.AsEnumerable().Reverse().ToArray();
            _entries.Clear();
        }

        foreach (var entry in entries)
        {
            StopEntry(entry);
        }
    }

    private static void StopEntry(Entry entry)
    {
        var process = entry.Process;
        try
        {
            if (process.HasExited) return;

            try
            {
                if (entry.GracefulStop != null)
                {
                    entry.GracefulStop(process);
                }
                else
                {
                    process.CloseMainWindow();
                }
            }
            catch (InvalidOperationException)
            {
                // The process may exit while a shutdown request is being sent.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Fall back to termination below.
            }

            if (!process.WaitForExit(2000))
            {
                process.Kill();
                process.WaitForExit(2000);
            }
        }
        catch (InvalidOperationException)
        {
            // The process has already exited or its handle is no longer available.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // A process can disappear between the state check and the operation.
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        StopAll();
    }
}
