using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using McpBridge.Services;

namespace McpBridge;

public partial class App : System.Windows.Application
{
    private static Mutex? _instanceMutex;
    internal static bool SkipUpdateCheck { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var isUpdaterHandoff = Array.Exists(e.Args, argument =>
            string.Equals(argument, "--update-handoff", StringComparison.OrdinalIgnoreCase));
        if (!isUpdaterHandoff && AppUpdateService.IsUpdaterRunning())
        {
            // A second Bridge instance must not open while the updater replaces files.
            // The updater launches the next Bridge instance after setup completes.
            Shutdown();
            return;
        }

        _instanceMutex = new Mutex(true, @"Local\MCPBridge", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        _instanceMutex?.Dispose();
        _instanceMutex = null;
    }
}
