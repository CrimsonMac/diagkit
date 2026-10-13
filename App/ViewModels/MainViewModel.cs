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
    Health,
    History,
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

    public MainViewModel(Func<string, object?> iconLoader, Func<string, bool> confirm)
    {
        _confirm = confirm;
        Tools = new ToolsViewModel(iconLoader, ShowNotice);
        History = new HistoryViewModel(OpenReport, ShowNotice, confirm);
        Health = new HealthViewModel(Tools, ShowNotice, () => _ = History.LoadAsync());
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
    public HealthViewModel Health { get; }
    public HistoryViewModel History { get; }
    public ICommand NavigateCommand { get; }
    public ICommand DismissNoticeCommand { get; }
    public ICommand FindCommand { get; }
    public ICommand RunCheckCommand { get; }
    public ICommand UninstallCommand { get; }

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
                AppPage.Health => Health,
                AppPage.History => History,
                _ => Tools,
            };
            OnPropertyChanged(nameof(IsToolsPage));
            OnPropertyChanged(nameof(IsHealthPage));
            OnPropertyChanged(nameof(IsHistoryPage));

            if (value == AppPage.Health && Health.IsSetup)
            {
                _ = Health.RefreshComponentsAsync();
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

    public async Task StartAsync()
    {
        await Tools.LoadAsync();
        _ = LeftoverCleanup.RunAsync();
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

    private void OpenReport(ReportViewModel report)
    {
        Health.ShowReport(report);
        Page = AppPage.Health;
    }
}
