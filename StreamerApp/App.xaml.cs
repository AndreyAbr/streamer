using System;
using System.IO;
using System.Windows;

namespace StreamerApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            File.WriteAllText("crash.log", $"AppDomain Unhandled: {args.ExceptionObject}");
        };

        DispatcherUnhandledException += (s, args) =>
        {
            File.WriteAllText("crash.log", $"Dispatcher Unhandled: {args.Exception}");
        };

        base.OnStartup(e);
    }
}

