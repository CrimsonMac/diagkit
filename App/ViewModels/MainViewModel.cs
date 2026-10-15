using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using DiagKit.Models;
using DiagKit.Services;

namespace DiagKit.ViewModels;

public enum AppPage
{
    Tools,
    Quick,
    Health,
    History,
    Settings,
}

public sealed class MainViewModel : ObservableObject
{
    private AppPage _page = AppPage.Tools;
    private object _currentPage;
    private string _noticeText = "";
    private LedState _noticeLed = LedState.Info;
    private bool _isNoticeVisible;
    private int _noticeVersion;
    private readonly Func<string, bool> _confirm;

    public MainViewModel(Func<string, object?> iconLoader, Func<string, bool> confirm, AppSettings settings)
    {
        _confirm = confirm;
        Tools = new ToolsViewModel(iconLoader, ShowNotice);
        History = new HistoryViewModel(ShowNotice, confirm);
        var gate = new CheckLock();
        Health = new HealthViewModel(Tools, ShowNotice, () => _ = History.LoadAsync(), settings, gate, "Back to the full check");
        Quick = new QuickViewModel(new HealthViewModel(Tools, ShowNotice, () => _ = History.LoadAsync(), settings, gate, "Back to quick actions"), settings);
        Settings = new SettingsViewModel(settings);
        Settings.Changed += Health.ApplyDefaults;
        Settings.Changed += Quick.ApplyDefaults;
        Settings.Changed += Quick.Runner.ApplyDefaults;
        _currentPage = Tools;
        IsElevated = SystemProbe.IsElevated();

        NavigateCommand = new RelayCommand(p =>
        {
            if (p is string s && Enum.TryParse(s, ignoreCase: true, out AppPage page))
            {
                Page = page;
            }
        });
        DismissNoticeCommand = new RelayCommand(() => IsNoticeVisible = false);
        FindCommand = new RelayCommand(() =>
        {
            Page = AppPage.Tools;
            Tools.RequestSearchFocus();
        });
        UninstallCommand = new RelayCommand(StartUninstall);
        RunCheckCommand = new RelayCommand(() =>
        {
            Page = AppPage.Health;
            if (Health.IsSetup && Health.RunCommand.CanExecute(null))
            {
                Health.RunCommand.Execute(null);
            }
        });
    }

    public ToolsViewModel Tools { get; }
    public QuickViewModel Quick { get; }
    public HealthViewModel Health { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }
    public ICommand NavigateCommand { get; }
    public ICommand DismissNoticeCommand { get; }
    public ICommand FindCommand { get; }
    public ICommand RunCheckCommand { get; }
    public ICommand UninstallCommand { get; }

    /// <summary>A check asked for on the command line; run on its page once the tools are found.</summary>
    public CheckArguments? StartupCheck { get; init; }

    /// <summary>Raised when DiagKit should close (Uninstall.exe was started and will clean up after it).</summary>
    public event Action? CloseRequested;

    public bool CanUninstall => System.IO.File.Exists(System.IO.Path.Combine(AppPaths.BaseDir, Kit.KitLayout.UninstallExe));
    public bool IsElevated { get; }
    public string MachineName => Environment.MachineName;
    public string VersionText => "v" + AppInfo.Version;
    public string ElevationText => IsElevated ? "Administrator" : "Not elevated: some tools and checks will fail";

    public AppPage Page
    {
        get => _page;
        set
        {
            if (!SetProperty(ref _page, value))
            {
                return;
            }

            CurrentPage = value switch
            {
                AppPage.Quick => Quick,
                AppPage.Health => Health,
                AppPage.History => History,
                AppPage.Settings => Settings,
                _ => Tools,
            };
            OnPropertyChanged(nameof(IsToolsPage));
            OnPropertyChanged(nameof(IsQuickPage));
            OnPropertyChanged(nameof(IsHealthPage));
            OnPropertyChanged(nameof(IsHistoryPage));
            OnPropertyChanged(nameof(IsSettingsPage));

            if (value == AppPage.Health && Health.IsSetup)
            {
                _ = Health.RefreshComponentsAsync();
            }
            else if (value == AppPage.Quick && Quick.Runner.IsSetup)
            {
                _ = Quick.Runner.RefreshComponentsAsync();
            }
            else if (value == AppPage.History)
            {
                _ = History.EnsureLoadedAsync();
            }
        }
    }

    // Two-way bindings for the navigation rail's radio buttons; unchecking is ignored.
    public bool IsToolsPage
    {
        get => _page == AppPage.Tools;
        set
        {
            if (value)
            {
                Page = AppPage.Tools;
            }
        }
    }

    public bool IsQuickPage
    {
        get => _page == AppPage.Quick;
        set
        {
            if (value)
            {
                Page = AppPage.Quick;
            }
        }
    }

    public bool IsHealthPage
    {
        get => _page == AppPage.Health;
        set
        {
            if (value)
            {
                Page = AppPage.Health;
            }
        }
    }

    public bool IsHistoryPage
    {
        get => _page == AppPage.History;
        set
        {
            if (value)
            {
                Page = AppPage.History;
            }
        }
    }

    public bool IsSettingsPage
    {
        get => _page == AppPage.Settings;
        set
        {
            if (value)
            {
                Page = AppPage.Settings;
            }
        }
    }

    public object CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string NoticeText
    {
        get => _noticeText;
        private set => SetProperty(ref _noticeText, value);
    }

    public LedState NoticeLed
    {
        get => _noticeLed;
        private set => SetProperty(ref _noticeLed, value);
    }

    public bool IsNoticeVisible
    {
        get => _isNoticeVisible;
        set => SetProperty(ref _isNoticeVisible, value);
    }

    public string RunningToolNames => string.Join(", ", Tools.RunningTools.Select(t => t.Name).Distinct());

    public bool HasRunningTools => Tools.RunningTools.Any();

    public bool IsCheckRunning => Health.IsAnyRunning;

    public async Task StartAsync()
    {
        // Started first: the Full check page waits on the tool scan when it is the start page.
        Task load = Tools.LoadAsync();
        Page = Settings.StartPage;
        await load;
        _ = LeftoverCleanup.RunAsync();
        if (StartupCheck is { } check)
        {
            await RunStartupCheckAsync(check);
        }
    }

    private Task RunStartupCheckAsync(CheckArguments check)
    {
        if (check.Only == null)
        {
            Page = AppPage.Health;
            return Health.RunAsync(new CheckRequest
            {
                StressSeconds = check.StressSeconds,
                StartupScan = check.StartupScan,
                InstallPawnIo = check.InstallPawnIo,
            });
        }

        // A hand-picked set runs on the Quick page, like Custom diagnosis, and is named the same way.
        Page = AppPage.Quick;
        return Quick.Runner.RunAsync(new CheckRequest
        {
            Only = check.Only,
            StressSeconds = check.StressSeconds,
            StartupScan = check.StartupScan,
            InstallPawnIo = check.InstallPawnIo && (check.Only.Contains(Report.CheckId.Sensors) || check.StressSeconds > 0),
            Scope = check.Scope,
            Title = $"Custom check of {Environment.MachineName}",
        });
    }

    public async void ShowNotice(string text, LedState led)
    {
        int version = ++_noticeVersion;
        NoticeText = text;
        NoticeLed = led;
        IsNoticeVisible = true;

        // Longer messages stay up longer; errors stay until dismissed or replaced.
        if (led == LedState.Critical)
        {
            return;
        }

        int ms = Compat.Clamp(3500 + text.Length * 45, 5000, 14000);
        await Task.Delay(ms);
        if (version == _noticeVersion)
        {
            IsNoticeVisible = false;
        }
    }

    private void StartUninstall()
    {
        if (!_confirm("Are you sure you want to run the uninstaller?\n\nDiagKit closes, and Uninstall.exe lets you choose between cleaning this PC and removing DiagKit completely."))
        {
            return;
        }

        string exe = System.IO.Path.Combine(AppPaths.BaseDir, Kit.KitLayout.UninstallExe);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppPaths.BaseDir })?.Dispose();
        }
        catch (Exception ex)
        {
            ShowNotice("Could not open the uninstaller: " + ex.Message, LedState.Critical);
            return;
        }

        // The uninstaller closes anything still running from the kit; closing DiagKit first lets it finish its own cleanup.
        CloseRequested?.Invoke();
    }
}
