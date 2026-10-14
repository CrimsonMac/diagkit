using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DiagKit.Kit;
using DiagKit.Models;
using DiagKit.ViewModels;

namespace DiagKit.Uninstall;

public enum UninstallStage
{
    Choose,
    Working,
    Done,
}

public sealed class UninstallViewModel : ObservableObject
{
    private readonly RemovalOptions _options;
    private readonly Remover _remover;
    private UninstallStage _stage = UninstallStage.Choose;
    private string _resultTitle = "";
    private string _resultText = "";
    private LedState _resultLed = LedState.Ok;
    private List<string> _willRemove = new();
    private List<string> _willKeep = new();
    private string _runningText = "";

    public UninstallViewModel(string kitDir, RemovalOptions options)
    {
        _options = options;
        _remover = new Remover(kitDir, line => Application.Current?.Dispatcher.BeginInvoke(new Action(() => Log.Add(line))));
        UninstallCommand = new AsyncCommand(RunAsync);
        CloseCommand = new RelayCommand(() => Application.Current?.MainWindow?.Close());
        Refresh();
    }

    public ICommand UninstallCommand { get; }
    public ICommand CloseCommand { get; }
    public ObservableCollection<string> Log { get; } = new();

    public string KitDir => _remover.KitDir;
    public string PcName => ThisMachine.Name;

    public UninstallStage Stage
    {
        get => _stage;
        private set
        {
            if (SetProperty(ref _stage, value))
            {
                OnPropertyChanged(nameof(IsChoosing));
                OnPropertyChanged(nameof(IsWorking));
                OnPropertyChanged(nameof(IsDone));
            }
        }
    }

    public bool IsChoosing => _stage == UninstallStage.Choose;
    public bool IsWorking => _stage == UninstallStage.Working;
    public bool IsDone => _stage == UninstallStage.Done;

    public bool RemoveKit
    {
        get => _options.RemoveKit;
        set
        {
            if (_options.RemoveKit != value)
            {
                _options.RemoveKit = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CleanOnly));
                OnPropertyChanged(nameof(ActionText));
                Refresh();
            }
        }
    }

    public bool CleanOnly
    {
        get => !RemoveKit;
        set => RemoveKit = !value;
    }

    public bool DeleteReports
    {
        get => _options.DeleteReports;
        set
        {
            if (_options.DeleteReports != value)
            {
                _options.DeleteReports = value;
                OnPropertyChanged();
                Refresh();
            }
        }
    }

    public bool RemovePawnIo
    {
        get => _options.RemovePawnIo;
        set
        {
            if (_options.RemovePawnIo != value)
            {
                _options.RemovePawnIo = value;
                OnPropertyChanged();
                Refresh();
            }
        }
    }

    public bool HasReports => _remover.ReportCount > 0;
    public string ReportsText => $"Delete the {_remover.ReportCount} saved {(_remover.ReportCount == 1 ? "report" : "reports")}";

    public bool ShowPawnIoOption => _remover.PawnIoInstalledByDiagKit;
    public string PawnIoText => $"Remove the PawnIO driver (DiagKit's health check installed it on {_remover.PawnIoInstallDate:d MMM yyyy})";

    public string ActionText => RemoveKit ? "Uninstall" : "Clean this PC";

    public List<string> WillRemove
    {
        get => _willRemove;
        private set => SetProperty(ref _willRemove, value);
    }

    public List<string> WillKeep
    {
        get => _willKeep;
        private set => SetProperty(ref _willKeep, value);
    }

    public string OtherPcsText
    {
        get
        {
            List<string> others = _remover.OtherPcsWithTraces;
            if (others.Count == 0)
            {
                return "";
            }

            return "DiagKit was also used on " + string.Join(", ", others) + ". Run Uninstall.exe on " +
                   (others.Count == 1 ? "that PC" : "those PCs") + (RemoveKit ? " before deleting the kit" : "") +
                   " to clean them too; this PC is the only one that can be cleaned from here.";
        }
    }

    public string RunningText
    {
        get => _runningText;
        private set => SetProperty(ref _runningText, value);
    }

    public string ResultTitle
    {
        get => _resultTitle;
        private set => SetProperty(ref _resultTitle, value);
    }

    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    public LedState ResultLed
    {
        get => _resultLed;
        private set => SetProperty(ref _resultLed, value);
    }

    private void Refresh()
    {
        var remove = new List<string>
        {
            "Windows' records of DiagKit and its tools having run on this PC (Prefetch, Explorer's launch history, compatibility caches, Recent Items)",
        };
        List<PcTrace> pending = _remover.PendingTraces;
        if (pending.Count > 0)
        {
            remove.Add($"{pending.Count} registry {(pending.Count == 1 ? "key or driver" : "keys and drivers")} a tool left behind: " +
                       string.Join(", ", pending.Select(p => p.Value).Take(4)) + (pending.Count > 4 ? ", …" : ""));
        }

        if (_remover.StartMenuShortcut != null)
        {
            remove.Add("The DiagKit shortcut in the Start menu");
        }

        if (ShowPawnIoOption && RemovePawnIo)
        {
            remove.Add("The PawnIO driver");
        }

        if (DeleteReports && Directory.Exists(Remover.LocalReportsDir))
        {
            remove.Add(@"Reports saved on this PC in Documents\DiagKit Reports");
        }

        if (RemoveKit)
        {
            List<string> tools = _remover.InstalledToolFolders().Select(Path.GetFileName).ToList()!;
            remove.Add("DiagKit.exe and this uninstaller");
            if (tools.Count > 0)
            {
                remove.Add($"The {tools.Count} tools DiagKit downloaded, with their settings: {string.Join(", ", tools)}");
            }

            if (DeleteReports && Directory.Exists(_remover.ReportsDir))
            {
                remove.Add("The kit's saved reports");
            }
        }

        var keep = new List<string>
        {
            "Programs and settings that were on this PC before DiagKit ran, such as your own copy of a tool and its settings",
        };
        if (!RemoveKit)
        {
            keep.Insert(0, $"The kit in {KitDir}, ready for the next PC");
        }

        if (ShowPawnIoOption && !RemovePawnIo)
        {
            keep.Add("The PawnIO driver");
        }
        else if (!ShowPawnIoOption && PawnIo.IsInstalled())
        {
            keep.Add("The PawnIO driver, which was already on this PC before DiagKit");
        }

        if (RemoveKit)
        {
            List<string> mine = _remover.UserFiles(DeleteReports);
            if (mine.Count > 0)
            {
                keep.Add("Files you added to the kit: " + string.Join(", ", mine.Take(5)) + (mine.Count > 5 ? $" and {mine.Count - 5} more" : ""));
            }
        }

        WillRemove = remove;
        WillKeep = keep;
        OnPropertyChanged(nameof(OtherPcsText));

        List<(int Id, string Path)> running = _remover.RunningFromKit();
        RunningText = running.Count == 0
            ? ""
            : string.Join(", ", running.Select(r => Path.GetFileName(r.Path)).Distinct(StringComparer.OrdinalIgnoreCase)) +
              (running.Count == 1 ? " is" : " are") + " still running from the kit and will be closed.";
    }

    private async Task RunAsync()
    {
        Stage = UninstallStage.Working;
        bool removeKit = RemoveKit;
        int problems = await Task.Run(() =>
        {
            _remover.CloseRunning();
            int n = _remover.CleanThisPc(_options);
            if (removeKit)
            {
                n += _remover.RemoveKitFiles(_options);
            }

            _remover.ScrubOwnPrefetch();
            return n;
        });

        if (problems == 0)
        {
            ResultLed = LedState.Ok;
            ResultTitle = removeKit ? "DiagKit has been removed" : $"{PcName} is clean";
            ResultText = removeKit
                ? "Everything DiagKit put on this PC and in the kit is gone. This window removes itself when you close it."
                : "Everything DiagKit and its tools left on this PC is gone. The kit is ready for the next PC.";
        }
        else
        {
            ResultLed = LedState.Warning;
            ResultTitle = "Almost done";
            ResultText = $"{problems} {(problems == 1 ? "item" : "items")} could not be removed (see the list below). " +
                         "A file may still be in use or a driver still loaded: restart the PC and run Uninstall again.";
        }

        _deleteSelfOnClose = removeKit;
        Stage = UninstallStage.Done;
    }

    private bool _deleteSelfOnClose;

    /// <summary>Called when the window closes: the uninstaller removes itself once it has exited.</summary>
    public void OnClosed()
    {
        if (_deleteSelfOnClose)
        {
            _remover.ScheduleSelfDelete();
        }
    }
}
