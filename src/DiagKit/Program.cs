using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DiagKit;

/// <summary>
/// Custom entry point so that failures during the very first steps of startup (loading the theme,
/// WPF's native DLLs, the main window) are logged and shown, instead of the process vanishing silently.
/// Deliberately depends on nothing else in the app.
/// </summary>
public static partial class Program
{
    [STAThread]
    public static int Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report("DiagKit crashed", e.ExceptionObject as Exception);
        try
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        catch (Exception ex)
        {
            Report("DiagKit failed to start", ex);
            return 1;
        }
    }

    internal static void Report(string context, Exception? ex)
    {
        string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
        string? logPath = TryAppend(Path.Combine(AppContext.BaseDirectory, "diagkit-error.log"), text)
                          ?? TryAppend(Path.Combine(Path.GetTempPath(), "diagkit-error.log"), text);

        // Native message box: works even when WPF itself is what failed to load.
        Exception? root = ex;
        while (root?.InnerException != null)
        {
            root = root.InnerException;
        }

        string message = $"{context}.\n\n{root?.GetType().Name}: {root?.Message}\n\n" +
                         (logPath != null ? $"Full details were written to:\n{logPath}" : "The error log could not be written.");
        try
        {
            MessageBoxW(IntPtr.Zero, message, "DiagKit", 0x10 /* MB_ICONERROR */);
        }
        catch
        {
            // No UI possible; the log (or the Windows event log) has it.
        }
    }

    private static string? TryAppend(string path, string text)
    {
        try
        {
            File.AppendAllText(path, text);
            return path;
        }
        catch
        {
            return null;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
