using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace DiagKit.Uninstall;

/// <summary>
/// Uninstall.exe [/clean] [/keep-reports] [/keep-pawnio] [/quiet]
///   (no switch)    remove DiagKit completely: this PC's traces and the kit's files
///   /clean         only clean this PC; the kit stays, ready for the next PC
///   /keep-reports  keep saved health-check reports
///   /keep-pawnio   keep the PawnIO driver even if DiagKit installed it
///   /quiet         no window; exit code 0 when everything was removed
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool Has(string name) => args.Any(a => string.Equals(a.TrimStart('/', '-'), name, StringComparison.OrdinalIgnoreCase));

        var options = new RemovalOptions
        {
            RemoveKit = !Has("clean"),
            DeleteReports = !Has("keep-reports"),
            RemovePawnIo = !Has("keep-pawnio"),
        };
        string kit = AppDomain.CurrentDomain.BaseDirectory;

        if (Has("quiet"))
        {
            var remover = new Remover(kit, _ => { });
            remover.CloseRunning();
            int problems = remover.CleanThisPc(options);
            if (options.RemoveKit)
            {
                problems += remover.RemoveKitFiles(options);
            }

            remover.ScrubOwnPrefetch();
            if (options.RemoveKit)
            {
                remover.ScheduleSelfDelete();
            }

            return problems == 0 ? 0 : 1;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Uninstall;component/Themes/Theme.xaml", UriKind.Relative) });
        var window = new MainWindow(new UninstallViewModel(kit, options));
        return app.Run(window);
    }
}
