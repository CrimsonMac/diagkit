using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using DiagKit.Kit;
using DiagKit.ViewModels;

namespace DiagKit.Setup;

/// <summary>
/// setup.exe [/target &lt;folder&gt;] [/tools name,name] [/force] [/quiet]
///   /target  where to put the kit: an existing kit, or a folder (a USB stick's root) to create diagkit.&lt;version&gt; in
///   /tools   update only these tools (names or folders, comma-separated) and leave the rest of the kit as it is
///   /force   download every tool again, even when it is up to date
///   /quiet   no window; writes progress to setup.log next to setup.exe and exits with 0 when every tool installed
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? Value(string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i].TrimStart('/', '-'), name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        bool Has(string name) => args.Any(a => string.Equals(a.TrimStart('/', '-'), name, StringComparison.OrdinalIgnoreCase));

        string? target = Value("target");
        bool force = Has("force");
        List<ToolSpec>? only = Value("tools") is { } list
            ? Catalog.All.Where(t => t.Required || list.Split(',').Any(n => n.Trim().Equals(t.Name, StringComparison.OrdinalIgnoreCase) ||
                                                                           n.Trim().Equals(t.Folder, StringComparison.OrdinalIgnoreCase))).ToList()
            : null;

        if (Has("quiet"))
        {
            return RunQuiet(target ?? Locations.DefaultParent(), only, force);
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/setup;component/Themes/Theme.xaml", UriKind.Relative) });
        AsyncCommand.UnhandledError = ex => MessageBox.Show(ex.Message, "DiagKit setup", MessageBoxButton.OK, MessageBoxImage.Error);
        var vm = new SetupViewModel(target ?? Locations.DefaultParent(), only, force);
        return app.Run(new MainWindow(vm));
    }

    private static int RunQuiet(string target, List<ToolSpec>? only, bool force)
    {
        // A console log is not visible from a GUI exe, so quiet runs write next to setup.exe (outside the kit).
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "setup.log");
        using var log = new StreamWriter(logPath, false, new System.Text.UTF8Encoding(true)) { AutoFlush = true };
        object gate = new();
        void Write(string line)
        {
            lock (gate)
            {
                log.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
            }
        }

        try
        {
            KitTarget kit = KitTarget.For(target);
            List<ToolSpec> tools = only ?? SetupViewModel.DefaultSelection(kit).ToList();
            Write($"{(kit.Existing ? "Updating" : "Creating")} {kit.KitDir}");
            var installer = new KitInstaller(kit, tools, force, p => Write($"{p.Tool.Name}: {p.State} {p.Detail}"), Write) { OnlyThese = only != null };
            InstallSummary s = installer.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            foreach (string note in s.Notes)
            {
                Write(note);
            }

            // A quiet run never adds a shortcut, but keeps an existing one pointing at the (possibly renamed) kit.
            if (StartMenu.IsRecorded(s.KitDir))
            {
                Write(StartMenu.Add(s.KitDir));
            }

            Write(s.Failed == 0 ? $"Kit ready: {s.KitDir}" : $"{s.Failed} tools failed; DiagKit works without them. Run setup again later.");
            return s.Failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Write("Setup failed: " + ex.Message);
            return 2;
        }
    }
}

/// <summary>Suggestions for where the kit goes.</summary>
public static class Locations
{
    /// <summary>
    /// setup.exe's own folder when it already sits on a USB stick, otherwise the first USB stick plugged in, otherwise
    /// setup.exe's folder.
    /// </summary>
    public static string DefaultParent()
    {
        string here = FullPath(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'));
        if (IsRemovable(here))
        {
            return here;
        }

        DriveInfo? usb = Removable().FirstOrDefault();
        return usb?.RootDirectory.FullName ?? here;
    }

    /// <summary>
    /// The full path of a folder, with a bare drive letter ("E:") read as the top of that drive. Windows takes "E:" to
    /// mean the current folder on E:, so setup.exe at the top of a USB stick but started from elsewhere on it would
    /// otherwise open the picker, and put the kit, in that folder instead.
    /// </summary>
    public static string FullPath(string path)
        => Path.GetFullPath(path.Length == 2 && path[1] == ':' ? path + "\\" : path);

    public static IEnumerable<DriveInfo> Removable()
    {
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            bool ok;
            try
            {
                ok = d.DriveType == DriveType.Removable && d.IsReady;
            }
            catch
            {
                ok = false;
            }

            if (ok)
            {
                yield return d;
            }
        }
    }

    public static bool IsRemovable(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Removable;
        }
        catch
        {
            return false;
        }
    }
}
