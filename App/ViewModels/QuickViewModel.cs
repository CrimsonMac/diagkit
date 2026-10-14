using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using DiagKit.Report;
using DiagKit.Services;

namespace DiagKit.ViewModels;

/// <summary>One way to start a quick action; most have one ("Run"), the load test one per duration.</summary>
public sealed record QuickRun(string Label, ICommand Command);

public sealed class QuickAction
{
    public QuickAction(string glyph, string title, string description, IReadOnlyList<QuickRun> runs)
    {
        Glyph = glyph;
        Title = title;
        Description = description;
        Runs = runs;
    }

    public string Glyph { get; }
    public string Title { get; }
    public string Description { get; }
    public IReadOnlyList<QuickRun> Runs { get; }
}

/// <summary>A tickable check in Custom diagnosis.</summary>
public sealed class CheckOption : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected = true;

    public CheckOption(CheckInfo info, Action changed)
    {
        Info = info;
        _changed = changed;
    }

    public CheckInfo Info { get; }
    public string Name => Info.Name;
    public string Description => Info.Description;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _changed();
            }
        }
    }
}

/// <summary>
/// The Quick actions page: single checks a click away, and Custom diagnosis for a hand-picked set, so seeing one
/// thing does not mean sitting through the whole health check. Both run through the Health page, which shows the
/// progress and the report.
/// </summary>
public sealed class QuickViewModel : ObservableObject
{
    private readonly Action _showHealth;
    private readonly AppSettings _settings;
    private readonly RelayCommand _runCustomCommand;
    private int _customStress;
    private bool _installPawnIo;

    public QuickViewModel(HealthViewModel health, Action showHealth, AppSettings settings)
    {
        Health = health;
        _showHealth = showHealth;
        _settings = settings;
        _installPawnIo = settings.OfferPawnIo;

        QuickRun Run(string label, string title, int stress, params CheckId[] checks)
            => new(label, new RelayCommand(() => Start(title, stress, checks), () => !Health.IsRunning));

        Actions = new List<QuickAction>
        {
            new("", "Drive health", "SMART health of every drive, and the space left on each.",
                new[] { Run("Run", "Drive health", 0, CheckId.Storage, CheckId.Volumes) }),
            new("", "Temperatures", "CPU and GPU temperatures, clocks, power draw and fan speeds at idle.",
                new[] { Run("Run", "Temperatures", 0, CheckId.Sensors) }),
            new("", "CPU load test", "Every core at full load, to catch overheating and throttling. Fans get loud.",
                new[] { Run("30 s", "CPU load test", 30), Run("2 min", "CPU load test", 120) }),
            new("", "Crashes and hardware errors", "Blue screens and the drivers behind them, hardware errors, power losses and disk errors from the event logs.",
                new[] { Run("Run", "Crashes and hardware errors", 0, CheckId.Events, CheckId.CrashDumps) }),
            new("", "Memory", "Installed RAM sticks, and whether they run at their rated speed.",
                new[] { Run("Run", "Memory", 0, CheckId.Memory) }),
            new("", "Devices", "Device Manager entries with a yellow warning: missing or failed drivers.",
                new[] { Run("Run", "Devices", 0, CheckId.Devices) }),
            new("", "Battery", "How much a laptop battery still holds compared with when it was new.",
                new[] { Run("Run", "Battery", 0, CheckId.Battery) }),
            new("", "Startup programs", "Unsigned, broken or unusual programs that start with Windows. Takes 20 to 60 seconds.",
                new[] { Run("Run", "Startup programs", 0, CheckId.Startup) }),
            new("", "System and Windows", "Model, BIOS, Windows version and support status, pending restarts and security settings.",
                new[] { Run("Run", "System and Windows", 0) }),
        };

        Checks = HealthChecks.Selectable.Select(c => new CheckOption(c, OnCustomChanged)).ToList();
        _runCustomCommand = new RelayCommand(StartCustom, () => !Health.IsRunning && (Checks.Any(c => c.IsSelected) || _customStress > 0));
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        Health.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HealthViewModel.IsRunning))
            {
                RefreshCommands();
            }
        };
    }

    public HealthViewModel Health { get; }
    public List<QuickAction> Actions { get; }
    public List<CheckOption> Checks { get; }
    public ICommand RunCustomCommand => _runCustomCommand;
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }

    public int CustomStress
    {
        get => _customStress;
        set
        {
            if (SetProperty(ref _customStress, value))
            {
                OnCustomChanged();
            }
        }
    }

    /// <summary>Install PawnIO first when a run reads temperatures; shown only when Health says it can.</summary>
    public bool InstallPawnIo
    {
        get => _installPawnIo;
        set => SetProperty(ref _installPawnIo, value);
    }

    public string CustomSummary
    {
        get
        {
            int n = Checks.Count(c => c.IsSelected) + (_customStress > 0 ? 1 : 0);
            if (n == 0)
            {
                return "Tick at least one check.";
            }

            bool startup = Checks.Any(c => c.IsSelected && c.Info.Id == CheckId.Startup);
            int low = 5 + (startup ? 20 : 0) + _customStress;
            int high = 25 + (startup ? 60 : 0) + _customStress;
            string time = high < 60 ? $"about {low} to {high} seconds" : $"about {Math.Max(1, low / 60.0):0.#} to {high / 60.0:0.#} minutes";
            return $"{TextUtil.Plural(n, "check")}, {time}.";
        }
    }

    /// <summary>Picks up a changed default from Settings.</summary>
    public void ApplyDefaults() => InstallPawnIo = _settings.OfferPawnIo;

    private void Start(string title, int stress, CheckId[] checks)
    {
        var only = new HashSet<CheckId>(checks);
        Run(only, stress, "Quick check: " + title, $"{title} on {Environment.MachineName}");
    }

    private void StartCustom()
    {
        var only = new HashSet<CheckId>(Checks.Where(c => c.IsSelected).Select(c => c.Info.Id));
        IEnumerable<string> names = Checks.Where(c => c.IsSelected).Select(c => c.Name);
        if (_customStress > 0)
        {
            names = names.Append($"CPU load test ({(_customStress >= 60 ? _customStress / 60 + " min" : _customStress + " s")})");
        }

        Run(only, _customStress, "Custom check: " + string.Join(", ", names), $"Custom check of {Environment.MachineName}");
    }

    private void Run(HashSet<CheckId> only, int stress, string scope, string title)
    {
        _showHealth();
        _ = Health.RunAsync(new CheckRequest
        {
            Only = only,
            StressSeconds = stress,
            StartupScan = only.Contains(CheckId.Startup),
            // The driver is only worth installing when the run reads temperatures.
            InstallPawnIo = _installPawnIo && (only.Contains(CheckId.Sensors) || stress > 0),
            Scope = scope,
            Title = title,
        });
    }

    private void SetAll(bool on)
    {
        foreach (CheckOption c in Checks)
        {
            c.IsSelected = on;
        }
    }

    private void OnCustomChanged()
    {
        OnPropertyChanged(nameof(CustomSummary));
        _runCustomCommand.RaiseCanExecuteChanged();
    }

    private void RefreshCommands()
    {
        _runCustomCommand.RaiseCanExecuteChanged();
        foreach (QuickRun run in Actions.SelectMany(a => a.Runs))
        {
            (run.Command as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
