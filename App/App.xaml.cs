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
        AsyncCommand.UnhandledError = ex => AppPaths.LogError("Command failed", ex);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppPaths.LogError("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        // Fatal AppDomain exceptions are logged and shown by Program.Main.

        // The palette is in place before the first window loads, so it never flashes the wrong theme.
        AppSettings settings = AppSettings.Load();
        ThemeManager.Apply(settings.Theme);

        var window = new MainWindow();
        var vm = new MainViewModel(IconLoader.Load, window.Confirm, settings);
        window.DataContext = vm;
        vm.CloseRequested += window.Close;
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThemeManager.Shutdown();
        base.OnExit(e);
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
