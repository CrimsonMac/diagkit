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
        // Fatal AppDomain exceptions are logged and shown by Program.Main.

        var window = new MainWindow();
        var vm = new MainViewModel(IconLoader.Load, window.Confirm);
        window.DataContext = vm;
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string? log = AppPaths.LogError("Unhandled UI exception", e.Exception);
        MessageBox.Show(
            "Something went wrong: " + e.Exception.Message +
            (log != null ? $"\n\nDetails were written to {log}." : "") + "\n\nDiagKit will keep running.",
            "DiagKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
