using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiagKit.Models;
using DiagKit.ViewModels;

namespace DiagKit.Report;

public enum StepState
{
    Pending,
    Running,
    Done,
    Partial,
    Failed,
    Skipped,
}

/// <summary>Live status of one health-check step. Property changes may come from worker threads.</summary>
public sealed class CollectorStatus : ObservableObject
{
    private StepState _state = StepState.Pending;
    private string _detail = "Waiting";
    private double _seconds;

    public CollectorStatus(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; }
    public string Description { get; }

    public StepState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(Led));
                OnPropertyChanged(nameof(SecondsText));
            }
        }
    }

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public double Seconds
    {
        get => _seconds;
        set
        {
            if (SetProperty(ref _seconds, value))
            {
                OnPropertyChanged(nameof(SecondsText));
            }
        }
    }

    public string SecondsText => State is StepState.Pending or StepState.Skipped ? "" : $"{Seconds:0.0} s";

    public LedState Led => State switch
    {
        StepState.Running => LedState.Busy,
        StepState.Done => LedState.Ok,
        StepState.Partial => LedState.Warning,
        StepState.Failed => LedState.Critical,
        _ => LedState.Off,
    };

    /// <summary>Set by the collector to mark that it finished with reduced coverage.</summary>
    public bool IsPartial { get; set; }
}

public sealed class ReportOptions
{
    public bool StressTest { get; set; }
    public int StressSeconds { get; set; } = 30;
    public bool StartupScan { get; set; } = true;

    /// <summary>Install the PawnIO driver first (only set when it is missing and its setup is in the kit).</summary>
    public bool InstallPawnIo { get; set; }

    /// <summary>
    /// The checks to run besides the system overview; null runs the full check. The load test and the startup scan
    /// still follow <see cref="StressTest"/> and <see cref="StartupScan"/>.
    /// </summary>
    public HashSet<CheckId>? Only { get; set; }

    /// <summary>What a partial check covered, in words ("Quick check: Drive health"); null for a full check.</summary>
    public string? Scope { get; set; }
}

/// <summary>Shared state for one health-check run.</summary>
public sealed class CollectorContext : IDisposable
{
    private readonly object _rawLock = new();

    public CollectorContext(DiagnosticReport report, string folder, IReadOnlyDictionary<string, string?> toolPaths,
        IReadOnlyDictionary<string, Models.ToolDefinition> toolDefinitions, ReportOptions options)
    {
        Report = report;
        Folder = folder;
        RawDir = Path.Combine(folder, "raw");
        ToolPaths = toolPaths;
        ToolDefinitions = toolDefinitions;
        Options = options;
        Directory.CreateDirectory(RawDir);
    }

    public DiagnosticReport Report { get; }
    public string Folder { get; }
    public string RawDir { get; }
    public IReadOnlyDictionary<string, string?> ToolPaths { get; }
    public IReadOnlyDictionary<string, Models.ToolDefinition> ToolDefinitions { get; }
    public ReportOptions Options { get; }

    /// <summary>Hardware sensor access, opened by the sensor step and reused by the load test.</summary>
    public HardwareMonitor? Hardware { get; set; }

    public string? ToolPath(string id) => ToolPaths.TryGetValue(id, out string? p) ? p : null;

    public Models.ToolDefinition? ToolDefinition(string id) => ToolDefinitions.TryGetValue(id, out var d) ? d : null;

    public string WriteRaw(string fileName, string content)
    {
        string path = Path.Combine(RawDir, fileName);
        lock (_rawLock)
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        return path;
    }

    public string RawPath(string fileName) => Path.Combine(RawDir, fileName);

    public void Dispose()
    {
        Hardware?.Dispose();
        Hardware = null;
    }
}

public interface ICollector
{
    string Name { get; }
    string Description { get; }

    /// <summary>Steps in the same phase run in parallel; phases run in order.</summary>
    int Phase { get; }

    TimeSpan Timeout { get; }

    Task CollectAsync(CollectorContext ctx, CollectorStatus status, CancellationToken ct);
}
