using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DiagKit.Interop;
using DiagKit.Services;
using DiagKit.ViewModels;

namespace DiagKit;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppPaths.LogError("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppPaths.LogError("Fatal", ex);
            }
        };

        var window = new MainWindow();
        var vm = new MainViewModel(IconLoader.Load, window.Confirm);
        window.DataContext = vm;
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppPaths.LogError("Unhandled UI exception", e.Exception);
        MessageBox.Show(
            "Something went wrong: " + e.Exception.Message + "\n\nDetails were written to diagkit-error.log next to DiagKit.exe. DiagKit will keep running.",
            "DiagKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
