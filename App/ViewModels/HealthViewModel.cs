using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using DiagKit.Models;
using DiagKit.Report;
using DiagKit.Report.Collectors;
using DiagKit.Services;

namespace DiagKit.ViewModels;

public enum HealthMode
{
    Setup,
    Running,
    Results,
}

public sealed record ComponentItem(LedState Led, string Name, string Text);

public sealed class HealthViewModel : ObservableObject
{
    private readonly ToolsViewModel _tools;
    private readonly Action<string, LedState> _notify;
    private readonly Action _reportsChanged;
    private HealthMode _mode = HealthMode.Setup;
    private ReportViewModel? _report;
    private int _stressSeconds;
    private bool _includeStartup = true;
    private bool _installPawnIo = true;
    private bool _canInstallPawnIo;
    private string _progressText = "";
    private string _runTitle = "";
    private List<ComponentItem> _components = new();
    private CancellationTokenSource? _cts;
    private CollectorStatus? _stressStatus;

    public HealthViewModel(ToolsViewModel tools, Action<string, LedState> notify, Action reportsChanged)
    {
        _tools = tools;
        _notify = notify;
        _reportsChanged = reportsChanged;
        IsElevated = SystemProbe.IsElevated();
        RunCommand = new AsyncCommand(RunAsync, () => Mode != HealthMode.Running);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => Mode == HealthMode.Running);
        ShowLastReportCommand = new RelayCommand(() =>
        {
            if (Report != null)
            {
                Mode = HealthMode.Results;
            }
        });
        NewCheckCommand = new RelayCommand(() =>
        {
            Mode = HealthMode.Setup;
            _ = RefreshComponentsAsync();
        });
    }

    public ObservableCollection<CollectorStatus> Steps { get; } = new();
    public ICommand RunCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand NewCheckCommand { get; }
    public ICommand ShowLastReportCommand { get; }
    public bool IsElevated { get; }
    public string MachineName => Environment.MachineName;
    public int EventWindowDays => EventLogCollector.Days;

    public HealthMode Mode
    {
        get => _mode;
        private set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsSetup));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsResults));
                (RunCommand as AsyncCommand)?.RaiseCanExecuteChanged();
                (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSetup => _mode == HealthMode.Setup;
    public bool IsRunning => _mode == HealthMode.Running;
    public bool IsResults => _mode == HealthMode.Results;

    public ReportViewModel? Report
    {
        get => _report;
        private set => SetProperty(ref _report, value);
    }

    public bool HasPreviousReport => _report != null;

    // Load test choice as three radio-friendly booleans.
    public bool StressOff
    {
        get => _stressSeconds == 0;
        set
        {
            if (value)
            {
                SetStress(0);
            }
        }
    }

    public bool Stress30
    {
        get => _stressSeconds == 30;
        set
        {
            if (value)
            {
                SetStress(30);
            }
        }
    }

    public bool Stress120
    {
        get => _stressSeconds == 120;
        set
        {
            if (value)
            {
                SetStress(120);
            }
        }
    }

    private void SetStress(int seconds)
    {
        _stressSeconds = seconds;
        OnPropertyChanged(nameof(StressOff));
        OnPropertyChanged(nameof(Stress30));
        OnPropertyChanged(nameof(Stress120));
        OnPropertyChanged(nameof(EstimateText));
    }

    public bool IncludeStartup
    {
        get => _includeStartup;
        set
        {
            if (SetProperty(ref _includeStartup, value))
            {
                OnPropertyChanged(nameof(EstimateText));
            }
        }
    }

    /// <summary>Offer to install PawnIO: only when it is missing on this PC and its setup is in the kit.</summary>
    public bool CanInstallPawnIo
    {
        get => _canInstallPawnIo;
        private set => SetProperty(ref _canInstallPawnIo, value);
    }

    public bool InstallPawnIo
    {
        get => _installPawnIo;
        set => SetProperty(ref _installPawnIo, value);
    }

    public string EstimateText
    {
        get
        {
            int low = 10 + (_includeStartup ? 20 : 0) + _stressSeconds;
            int high = 25 + (_includeStartup ? 60 : 0) + _stressSeconds;
            return high < 60 ? $"Takes about {low} to {high} seconds." : $"Takes about {Math.Max(1, low / 60.0):0.#} to {high / 60.0:0.#} minutes.";
        }
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string RunTitle
    {
        get => _runTitle;
        private set => SetProperty(ref _runTitle, value);
    }

    public List<ComponentItem> Components
    {
        get => _components;
        private set => SetProperty(ref _components, value);
    }

    public async Task RefreshComponentsAsync()
    {
        await _tools.LoadTask;
        IReadOnlyDictionary<string, string?> paths = _tools.ToolPaths();
        bool pawn = await Task.Run(SystemProbe.IsPawnIoInstalled);
        bool pawnSetup = paths.TryGetValue("pawnio", out string? setup) && setup != null;
        CanInstallPawnIo = !pawn && pawnSetup;
        string pawnText = pawn ? "Installed: CPU temperatures available"
            : pawnSetup ? "Not installed on this PC: tick 'Install the CPU temperature driver' above to add it"
            : "Not installed, and PawnIO setup is not in the kit: no CPU temperatures (setup.exe downloads it)";

        ComponentItem Item(string id, string name, string have, string missing)
        {
            bool ok = paths.TryGetValue(id, out string? p) && p != null;
            return new ComponentItem(ok ? LedState.Ok : LedState.Off, name, ok ? have : missing);
        }

        Components = new List<ComponentItem>
        {
            Item("smartctl", "smartctl", "Full SMART data for every drive", "Missing: only Windows' basic drive status (add Tools\\smartmontools\\smartctl.exe)"),
            Item("bluescreenview", "BlueScreenView", "Names the driver behind each crash", "Missing: crashes are counted but not attributed"),
            Item("autorunsc", "autorunsc", "Checks startup entries for signatures", "Missing: startup scan is skipped (comes in the Autoruns zip)"),
            new(pawn ? LedState.Ok : LedState.Off, "PawnIO driver", pawnText),
        };
    }

    public void ShowReport(ReportViewModel report)
    {
        Report = report;
        OnPropertyChanged(nameof(HasPreviousReport));
        Mode = HealthMode.Results;
    }

    private async Task RunAsync()
    {
        await _tools.LoadTask;
        // F5 can start a check before the setup page has looked at this PC; the PawnIO option depends on it.
        await RefreshComponentsAsync();
        var options = new ReportOptions
        {
            StressTest = _stressSeconds > 0,
            StressSeconds = Math.Max(_stressSeconds, 10),
            StartupScan = _includeStartup,
            InstallPawnIo = _installPawnIo && _canInstallPawnIo,
        };

        List<PlannedStep> plan = ReportRunner.Plan(options);
        _stressStatus = plan.FirstOrDefault(s => s.Collector is StressCollector)?.Status;
        foreach (CollectorStatus old in Steps)
        {
            old.PropertyChanged -= OnStepChanged;
        }

        Steps.Clear();
        foreach (PlannedStep step in plan)
        {
            step.Status.PropertyChanged += OnStepChanged;
            Steps.Add(step.Status);
        }

        RunTitle = $"Checking {Environment.MachineName}";
        UpdateProgress();
        Mode = HealthMode.Running;

        IReadOnlyDictionary<string, string?> paths = _tools.ToolPaths();
        IReadOnlyDictionary<string, ToolDefinition> defs = _tools.ToolDefinitions();
        _cts = new CancellationTokenSource();
        try
        {
            ReportRunResult result = await Task.Run(() => ReportRunner.RunAsync(plan, options, paths, defs, _cts.Token));
            ShowReport(new ReportViewModel(result.Report, result.Folder, result.HtmlPath, _notify));
            if (result.SaveError != null)
            {
                _notify("The report could not be saved: " + result.SaveError, LedState.Warning);
            }
            else if (!AppPaths.ReportsAreOnKit)
            {
                _notify("The kit folder is read-only, so the report was saved to Documents\\DiagKit Reports on this PC.", LedState.Info);
            }

            _reportsChanged();
        }
        catch (OperationCanceledException)
        {
            Mode = _report != null ? HealthMode.Results : HealthMode.Setup;
            _notify("Health check cancelled. Nothing was saved.", LedState.Info);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("Health check failed", ex);
            Mode = HealthMode.Setup;
            _notify("The health check failed: " + ex.Message, LedState.Critical);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    private void OnStepChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CollectorStatus.State))
        {
            UpdateProgress();
        }
    }

    private void UpdateProgress()
    {
        // Called from worker threads too; reading a snapshot of the states is enough here.
        CollectorStatus[] steps = Steps.ToArray();
        int done = steps.Count(s => s.State is StepState.Done or StepState.Partial or StepState.Failed or StepState.Skipped);
        ProgressText = done == steps.Length
            ? "Writing the report"
            : _stressStatus?.State == StepState.Running
                ? "Running the CPU load test. The PC will get louder for a moment."
                : $"{done} of {steps.Length} steps done";
    }
}
