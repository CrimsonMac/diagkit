using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DiagKit;

/// <summary>
/// Custom entry point so that failures during the very first steps of startup (loading the theme,
/// the main window) are logged and shown, instead of the process vanishing silently.
/// Deliberately depends on nothing else in the app.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must be in place before any type from a referenced library is touched.
        AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report("DiagKit crashed", e.ExceptionObject as Exception);
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Report("DiagKit failed to start", ex);
            return 1;
        }
    }

    // Separate method so the JIT only resolves App (and everything it references) after the resolver is hooked up.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        CheckArguments? check;
        try
        {
            check = CheckArguments.Parse(args);
        }
        catch (ArgumentException ex)
        {
            if (args.Any(a => string.Equals(a.TrimStart('/', '-'), "quiet", StringComparison.OrdinalIgnoreCase)))
            {
                QuietCheck.WriteError(ex.Message);
            }
            else
            {
                MessageBoxW(IntPtr.Zero, ex.Message, "DiagKit", 0x30 /* MB_ICONWARNING */);
            }

            return 3;
        }

        if (check is { Quiet: true })
        {
            return QuietCheck.Run(check);
        }

        var app = new App { StartupCheck = check };
        app.InitializeComponent();
        return app.Run();
    }

    private static readonly Dictionary<string, Assembly?> Loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The libraries DiagKit uses are embedded in the exe as "deps/&lt;name&gt;.dll" resources (see DiagKit.csproj)
    /// and loaded from memory, so nothing is extracted to disk. Any requested version is answered with the
    /// embedded copy, which also takes the place of the binding redirects an exe.config would normally carry.
    /// </summary>
    private static Assembly? ResolveEmbedded(object? sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name ?? "";
        lock (Loaded)
        {
            if (Loaded.TryGetValue(name, out Assembly? cached))
            {
                return cached;
            }

            Assembly? result = null;
            using (Stream? s = typeof(Program).Assembly.GetManifestResourceStream("deps/" + name + ".dll"))
            {
                if (s != null)
                {
                    var bytes = new byte[s.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = s.Read(bytes, read, bytes.Length - read);
                        if (n == 0)
                        {
                            break;
                        }

                        read += n;
                    }

                    result = Assembly.Load(bytes);
                }
            }

            Loaded[name] = result;
            return result;
        }
    }

    internal static void Report(string context, Exception? ex)
    {
        string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
        string? logPath = TryAppend(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagkit-error.log"), text)
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

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
