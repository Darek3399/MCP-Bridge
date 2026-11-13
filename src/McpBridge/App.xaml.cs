using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using McpBridge.Services;

namespace McpBridge;

public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = @"Local\MCPBridge";
    private const string ActivateEventName = @"Local\MCPBridge.Activate";

    private static Mutex? _instanceMutex;
    private static bool _ownsInstanceMutex;
    private static EventWaitHandle? _activateEvent;
    private static RegisteredWaitHandle? _activateRegistration;

    internal static bool SkipUpdateCheck { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var isUpdaterHandoff = Array.Exists(e.Args, argument =>
            string.Equals(argument, "--update-handoff", StringComparison.OrdinalIgnoreCase));
        var isPostInstallLaunch = Array.Exists(e.Args, argument =>
            string.Equals(argument, "--postinstall-launch", StringComparison.OrdinalIgnoreCase));

        if (!isUpdaterHandoff && !isPostInstallLaunch && AppUpdateService.IsUpdaterRunning())
        {
            Shutdown();
            return;
        }

        _instanceMutex = new Mutex(false, InstanceMutexName);
        var waitTimeout = (isUpdaterHandoff || isPostInstallLaunch)
            ? TimeSpan.FromSeconds(5)
            : TimeSpan.FromMilliseconds(1500);

        try
        {
            _ownsInstanceMutex = _instanceMutex.WaitOne(waitTimeout, false);
        }
        catch (AbandonedMutexException)
        {
            _ownsInstanceMutex = true;
        }

        if (!_ownsInstanceMutex)
        {
            SignalExistingInstance();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        StartActivationListener();

        AppPaths.EnsureDirectories();
        SkipUpdateCheck = Array.Exists(e.Args, argument =>
            string.Equals(argument, "--skip-update-check", StringComparison.OrdinalIgnoreCase));
        if (isUpdaterHandoff)
            AppUpdateService.CompleteUpdaterHandoff(e.Args);
        else
            AppUpdateService.CleanupStaleUpdaterWorkers();

        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLog.Write("App.DispatcherUnhandledException", args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                ErrorLog.Write("AppDomain.UnhandledException", exception);
            else
                ErrorLog.WriteMessage(
                    "AppDomain.UnhandledException",
                    "ExceptionObjectType=" + (args.ExceptionObject?.GetType().FullName ?? "null") +
                    " IsTerminating=" + args.IsTerminating);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write("TaskScheduler.UnobservedTaskException", args.Exception);
        };

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        window.Activate();
    }

    private void StartActivationListener()
    {
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activateRegistration = ThreadPool.RegisterWaitForSingleObject(
                _activateEvent,
                (_, _) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var window = MainWindow;
                        if (window == null) return;
                        window.ShowInTaskbar = true;
                        if (!window.IsVisible) window.Show();
                        if (window.WindowState == WindowState.Minimized)
                            window.WindowState = WindowState.Normal;
                        window.Activate();
                    }));
                },
                null,
                Timeout.Infinite,
                false);
        }
        catch (Exception exception)
        {
            ErrorLog.Write("App.StartActivationListener", exception);
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var activateEvent = EventWaitHandle.OpenExisting(ActivateEventName);
            activateEvent.Set();
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);

        try { _activateRegistration?.Unregister(null); } catch { }
        _activateRegistration = null;

        try { _activateEvent?.Dispose(); } catch { }
        _activateEvent = null;

        if (_instanceMutex != null)
        {
            if (_ownsInstanceMutex)
            {
                try { _instanceMutex.ReleaseMutex(); } catch { }
                _ownsInstanceMutex = false;
            }
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }
    }
}