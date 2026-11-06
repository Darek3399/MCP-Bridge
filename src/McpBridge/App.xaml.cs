using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using McpBridge.Services;

namespace McpBridge;

public partial class App : System.Windows.Application
{
    private static Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, @"Local\MCPBridge", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        AppPaths.EnsureDirectories();

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