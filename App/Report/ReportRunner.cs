using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiagKit.Models;
using DiagKit.Report.Collectors;
using DiagKit.Services;

namespace DiagKit.Report;

public sealed class PlannedStep
{
    public PlannedStep(ICollector collector)
    {
        Collector = collector;
        Status = new CollectorStatus(collector.Name, collector.Description);
    }

    public ICollector Collector { get; }
    public CollectorStatus Status { get; }
}

public sealed class ReportRunResult
{
    public required DiagnosticReport Report { get; init; }
    public required string Folder { get; init; }
    public string? HtmlPath { get; init; }
    public string? SaveError { get; init; }
}

public static class ReportRunner
{
    /// <summary>Builds the step list. Call on the UI thread so the statuses can be bound directly.</summary>
    public static List<PlannedStep> Plan(ReportOptions options)
    {
        bool Want(CheckId id) => options.Only == null || options.Only.Contains(id);

        var collectors = new List<ICollector> { new SystemCollector() };
        // The load test reads temperatures through the sensor step's hardware monitor, so it brings that step along.
        if (Want(CheckId.Sensors) || options.StressTest)
        {
            collectors.Add(new SensorCollector());
        }

        if (Want(CheckId.Memory))
        {
            collectors.Add(new MemoryCollector());
        }

        if (Want(CheckId.Volumes))
        {
            collectors.Add(new VolumeCollector());
        }

        if (Want(CheckId.Storage))
        {
            collectors.Add(new StorageCollector());
        }

        if (Want(CheckId.Events))
        {
            collectors.Add(new EventLogCollector());
        }

        if (Want(CheckId.Reliability))
        {
            collectors.Add(new ReliabilityCollector());
        }

        if (Want(CheckId.CrashDumps))
        {
            collectors.Add(new CrashDumpCollector());
        }

        if (Want(CheckId.Devices))
        {
            collectors.Add(new DeviceCollector());
        }

        if (Want(CheckId.Battery))
        {
            collectors.Add(new BatteryCollector());
        }

        if (options.InstallPawnIo)
        {
            collectors.Add(new PawnIoSetupCollector());
        }

        if (options.StartupScan)
        {
            collectors.Add(new AutorunsCollector());
        }

        if (options.StressTest)
        {
            collectors.Add(new StressCollector(options.StressSeconds));
        }

        return collectors.OrderBy(c => c.Phase).Select(c => new PlannedStep(c)).ToList();
    }

    public static async Task<ReportRunResult> RunAsync(
        IReadOnlyList<PlannedStep> steps,
        ReportOptions options,
        IReadOnlyDictionary<string, string?> toolPaths,
        IReadOnlyDictionary<string, ToolDefinition> toolDefinitions,
        CancellationToken ct)
    {
        DateTime started = DateTime.Now;
        var total = Stopwatch.StartNew();
        var report = new DiagnosticReport
        {
            CreatedAt = started,
            MachineName = Environment.MachineName,
            AppVersion = AppInfo.Version,
            Scope = options.Scope,
        };

        string folder = ReportStore.CreateFolder(report.MachineName, started);
        var records = new List<CollectorRecord>();
        using var ctx = new CollectorContext(report, folder, toolPaths, toolDefinitions, options);

        try
        {
            foreach (IGrouping<int, PlannedStep> phase in steps.GroupBy(s => s.Collector.Phase).OrderBy(g => g.Key))
            {
                await Task.WhenAll(phase.Select(s => RunStepAsync(s, ctx, records, ct))).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(folder);
            throw;
        }

        // Rules see the complete report; a bug in one rule must not lose the whole run.
        try
        {
            report.Findings = HealthRules.Evaluate(report);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("Rules failed", ex);
            report.Findings = new List<Finding>
            {
                new()
                {
                    Id = "rules-error", Severity = Severity.Info, Category = "DiagKit",
                    Title = "Some checks could not be evaluated",
                    Detail = "DiagKit hit an internal error while interpreting the data: " + ex.Message,
                    Action = "The raw data is still in the report folder. Details are in diagkit-error.log.",
                },
            };
        }

        report.Collectors = steps.Select(s => records.FirstOrDefault(r => r.Name == s.Collector.Name))
            .Where(r => r != null).Select(r => r!).ToList();
        report.DurationSeconds = Math.Round(total.Elapsed.TotalSeconds, 1);
        ReportComparer.Attach(report, folder);

        string? html = null;
        string? saveError = null;
        try
        {
            html = ReportStore.Save(report, folder);
        }
        catch (Exception ex)
        {
            saveError = ex.Message;
            AppPaths.LogError("Saving report failed", ex);
        }

        return new ReportRunResult { Report = report, Folder = folder, HtmlPath = html, SaveError = saveError };
    }

    private static async Task RunStepAsync(PlannedStep step, CollectorContext ctx, List<CollectorRecord> records, CancellationToken ct)
    {
        CollectorStatus status = step.Status;
        ICollector collector = step.Collector;
        var sw = Stopwatch.StartNew();
        status.State = StepState.Running;
        status.Detail = "Working";

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var ticker = new Timer(_ => status.Seconds = sw.Elapsed.TotalSeconds, null, 0, 200);
        string recordStatus;
        try
        {
            Task work = Task.Run(() => collector.CollectAsync(ctx, status, linked.Token), CancellationToken.None);
            await work.WaitAsync(collector.Timeout, ct).ConfigureAwait(false);
            status.State = status.IsPartial ? StepState.Partial : StepState.Done;
            recordStatus = status.IsPartial ? "partial" : "ok";
        }
        catch (TimeoutException)
        {
            linked.Cancel();
            status.State = StepState.Failed;
            status.Detail = $"Stopped after {collector.Timeout.TotalSeconds:0} s without finishing";
            recordStatus = "timeout";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status.State = StepState.Skipped;
            status.Detail = "Cancelled";
            throw;
        }
        catch (Exception ex)
        {
            status.State = StepState.Failed;
            status.Detail = Friendly(ex);
            recordStatus = "failed";
            AppPaths.LogError($"Collector '{collector.Name}' failed", ex);
        }
        finally
        {
            sw.Stop();
            ticker.Change(Timeout.Infinite, Timeout.Infinite);
            status.Seconds = sw.Elapsed.TotalSeconds;
        }

        lock (records)
        {
            records.Add(new CollectorRecord
            {
                Name = collector.Name,
                Status = recordStatus,
                Message = status.Detail,
                Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
            });
        }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Access denied. Is DiagKit running as administrator?",
        System.Management.ManagementException me => "Windows Management (WMI) error: " + me.Message.Trim(),
        FileNotFoundException fnf when fnf.FileName?.Contains("LibreHardwareMonitor", StringComparison.OrdinalIgnoreCase) == true
            => "Sensor library is missing from this build",
        _ => ex.Message,
    };

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // Leftover folder is harmless.
        }
    }
}
