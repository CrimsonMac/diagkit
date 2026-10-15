using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DiagKit.Models;
using DiagKit.Report;
using DiagKit.Services;

namespace DiagKit;

/// <summary>
/// DiagKit.exe [/check full|step,step] [/stress &lt;seconds&gt;] [/nostartup] [/pawnio] [/quiet]
///   /check      run a check as DiagKit starts: full, or a comma-separated list of steps (see <see cref="StepNames"/>)
///   /stress     add the CPU load test, 10 to 600 seconds
///   /nostartup  leave the startup scan out of a full check
///   /pawnio     install the CPU temperature driver first when it is missing (never done without asking for it)
///   /quiet      no window: runs the check (the full check when /check is not given), writes diagkit-check.log next to
///               DiagKit.exe and exits with 0 (nothing worth fixing), 1 (worth fixing), 2 (critical) or 3 (no report)
/// </summary>
public sealed class CheckArguments
{
    /// <summary>Short names for the steps; a step's <see cref="CheckId"/> name works too, so new steps need no entry.</summary>
    public static readonly IReadOnlyDictionary<string, CheckId> StepNames = new Dictionary<string, CheckId>(StringComparer.OrdinalIgnoreCase)
    {
        ["drives"] = CheckId.Storage,
        ["space"] = CheckId.Volumes,
        ["temps"] = CheckId.Sensors,
        ["memory"] = CheckId.Memory,
        ["events"] = CheckId.Events,
        ["dumps"] = CheckId.CrashDumps,
        ["devices"] = CheckId.Devices,
        ["battery"] = CheckId.Battery,
        ["startup"] = CheckId.Startup,
    };

    /// <summary>The steps to run besides the system overview; null for the full check.</summary>
    public HashSet<CheckId>? Only { get; private set; }

    public int StressSeconds { get; private set; }
    public bool StartupScan { get; private set; }
    public bool InstallPawnIo { get; private set; }
    public bool Quiet { get; private set; }

    /// <summary>The report's scope, worded like Custom diagnosis; null for the full check.</summary>
    public string? Scope => Only == null ? null : HealthChecks.CustomScope(Only, StressSeconds);

    /// <summary>Null when no check was asked for. Throws <see cref="ArgumentException"/> with a message for the user.</summary>
    public static CheckArguments? Parse(string[] args)
    {
        string? Value(string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i].TrimStart('/', '-'), name, StringComparison.OrdinalIgnoreCase))
                {
                    return i + 1 < args.Length && !args[i + 1].StartsWith("/") ? args[i + 1] : throw new ArgumentException($"/{name} needs a value.");
                }
            }

            return null;
        }

        bool Has(string name) => args.Any(a => string.Equals(a.TrimStart('/', '-'), name, StringComparison.OrdinalIgnoreCase));

        string? check = Value("check");
        string? stress = Value("stress");
        bool quiet = Has("quiet");
        if (check == null && stress == null && !quiet)
        {
            return null;
        }

        var result = new CheckArguments { Quiet = quiet, InstallPawnIo = Has("pawnio") };
        if (stress != null)
        {
            if (!int.TryParse(stress, out int seconds) || seconds < 10 || seconds > 600)
            {
                throw new ArgumentException($"/stress takes a number of seconds from 10 to 600, not '{stress}'.");
            }

            result.StressSeconds = seconds;
        }

        if (check == null && stress != null)
        {
            // Only the load test (and the system overview every report has).
            result.Only = new HashSet<CheckId>();
        }
        else if (check != null && !check.Equals("full", StringComparison.OrdinalIgnoreCase))
        {
            var only = new HashSet<CheckId>();
            foreach (string name in check.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()))
            {
                if (StepNames.TryGetValue(name, out CheckId id) ||
                    (Enum.TryParse(name, ignoreCase: true, out id) && HealthChecks.Selectable.Any(c => c.Id == id) && !int.TryParse(name, out _)))
                {
                    only.Add(id);
                }
                else
                {
                    throw new ArgumentException($"Unknown step '{name}'. Use full, or any of: {string.Join(", ", StepNames.Keys)}.");
                }
            }

            result.Only = only;
        }

        result.StartupScan = result.Only?.Contains(CheckId.Startup) ?? !Has("nostartup");
        return result;
    }
}

/// <summary>Runs a check without a window, for scripts and scheduled tasks.</summary>
public static class QuietCheck
{
    public static int Run(CheckArguments args)
    {
        using var log = OpenLog();
        void Write(string line) => log?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");

        try
        {
            Write($"DiagKit {AppInfo.Version}: {args.Scope ?? "Full health check"} of {Environment.MachineName}");
            ManifestLoadResult manifest = ToolCatalog.LoadManifest();
            if (manifest.Warning != null)
            {
                Write(manifest.Warning);
            }

            List<ToolDefinition> tools = manifest.Manifest.Tools;
            List<string?> found = ToolCatalog.ResolveAll(tools);
            var paths = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var definitions = new Dictionary<string, ToolDefinition>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < tools.Count; i++)
            {
                if (!paths.ContainsKey(tools[i].Id))
                {
                    paths[tools[i].Id] = found[i];
                    definitions[tools[i].Id] = tools[i];
                }
            }

            // As on the Quick page, the driver is only worth installing when the run reads temperatures.
            bool readsTemps = args.Only == null || args.Only.Contains(CheckId.Sensors) || args.StressSeconds > 0;
            var options = new ReportOptions
            {
                StressTest = args.StressSeconds > 0,
                StressSeconds = Math.Max(args.StressSeconds, 10),
                StartupScan = args.StartupScan,
                InstallPawnIo = args.InstallPawnIo && readsTemps && !SystemProbe.IsPawnIoInstalled() &&
                                paths.TryGetValue("pawnio", out string? pawn) && pawn != null,
                Only = args.Only,
                Scope = args.Scope,
            };

            List<PlannedStep> plan = ReportRunner.Plan(options);
            ReportRunResult result = ReportRunner.RunAsync(plan, options, paths, definitions, CancellationToken.None).GetAwaiter().GetResult();
            DiagnosticReport report = result.Report;
            foreach (CollectorRecord c in report.Collectors)
            {
                Write($"{c.Name}: {c.Status}, {c.Message} ({c.Seconds:0.0} s)");
            }

            foreach (Finding f in report.Findings.Where(f => f.Severity > Severity.Ok))
            {
                Write($"{f.Severity}: {f.Title}");
            }

            Write(ReportFacts.Verdict(report));
            if (result.SaveError != null)
            {
                Write("The report could not be saved: " + result.SaveError);
                return 3;
            }

            Write("Report: " + result.Folder);
            return ReportFacts.Worst(report) switch
            {
                Severity.Critical => 2,
                Severity.Warning => 1,
                _ => 0,
            };
        }
        catch (Exception ex)
        {
            AppPaths.LogError("Command-line check failed", ex);
            Write("The check failed: " + ex.Message);
            return 3;
        }
    }

    /// <summary>Records a bad command line where a quiet run's output goes.</summary>
    public static void WriteError(string message)
    {
        using var log = OpenLog();
        log?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    /// <summary>diagkit-check.log next to DiagKit.exe, or in %TEMP% on a read-only kit (Uninstall removes it from both).</summary>
    private static StreamWriter? OpenLog()
    {
        foreach (string dir in new[] { AppPaths.BaseDir, Path.GetTempPath() })
        {
            try
            {
                return new StreamWriter(Path.Combine(dir, Kit.KitLayout.CheckLog), false, new UTF8Encoding(true)) { AutoFlush = true };
            }
            catch
            {
                // Try the next location.
            }
        }

        return null;
    }
}
