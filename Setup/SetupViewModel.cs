using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DiagKit.Kit;
using DiagKit.Models;
using DiagKit.ViewModels;

namespace DiagKit.Setup;

public sealed class ToolRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _selected;
    private LedState _led = LedState.Off;
    private string _detail = "";

    public ToolRowViewModel(ToolSpec spec, bool selected, bool installed, Action changed)
    {
        Spec = spec;
        _selected = selected || spec.Required;
        IsInstalled = installed;
        _changed = changed;
    }

    public ToolSpec Spec { get; }
    public string Name => Spec.Name;
    public bool IsRequired => Spec.Required;
    public bool CanChange => !Spec.Required;
    public bool IsInstalled { get; }

    public string Note => Spec.Required
        ? "used by the health check"
        : IsInstalled ? "in the kit" : "";

    public string SizeText => Spec.SizeMb >= 1 ? $"{Spec.SizeMb:0} MB" : $"{Spec.SizeMb * 1024:0} KB";

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value || Spec.Required))
            {
                _changed();
            }
        }
    }

    public LedState Led
    {
        get => _led;
        set => SetProperty(ref _led, value);
    }

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }
}

public sealed class ToolGroupRows
{
    public ToolGroupRows(string name, List<ToolRowViewModel> tools)
    {
        Name = name;
        Tools = tools;
    }

    public string Name { get; }
    public List<ToolRowViewModel> Tools { get; }
}

public enum SetupStage
{
    Choose,
    Working,
    Done,
}

public sealed class SetupViewModel : ObservableObject
{
    private readonly bool _force;
    private readonly List<ToolSpec>? _only;
    private SetupStage _stage = SetupStage.Choose;
    private string _parent = "";
    private KitTarget _target = null!;
    private List<ToolRowViewModel> _rows = new();
    private List<ToolGroupRows> _groups = new();
    private bool _redownload;
    private string _resultTitle = "";
    private string _resultText = "";
    private LedState _resultLed = LedState.Ok;
    private string _activity = "";
    private CancellationTokenSource? _cts;

    public SetupViewModel(string parent, List<ToolSpec>? only, bool force)
    {
        _only = only;
        _force = force;
        _redownload = force;
        InstallCommand = new AsyncCommand(RunAsync);
        BrowseCommand = new RelayCommand(Browse);
        UseDriveCommand = new RelayCommand(p =>
        {
            if (p is string root)
            {
                Parent = root;
            }
        });
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        CloseCommand = new RelayCommand(() => Application.Current?.MainWindow?.Close());
        OpenKitCommand = new RelayCommand(() => Start(Path.Combine(KitDir, KitLayout.AppExe)));
        OpenFolderCommand = new RelayCommand(() => Start(KitDir));
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
        Parent = parent;
    }

    public ICommand InstallCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand UseDriveCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand OpenKitCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CancelCommand { get; }

    public string Title => "Set up DiagKit " + SetupInfo.Version;

    public ObservableCollection<string> Log { get; } = new();

    public List<DriveChoice> Drives { get; } = Locations.Removable().Select(d => new DriveChoice(d)).ToList();
    public bool HasDrives => Drives.Count > 0;

    public SetupStage Stage
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

    public bool IsChoosing => _stage == SetupStage.Choose;
    public bool IsWorking => _stage == SetupStage.Working;
    public bool IsDone => _stage == SetupStage.Done;

    /// <summary>The folder the user picked (a USB stick's root, or an existing kit).</summary>
    public string Parent
    {
        get => _parent;
        set
        {
            if (!SetProperty(ref _parent, value))
            {
                return;
            }

            _target = KitTarget.For(value);
            HashSet<string> installed = KitInstaller.InstalledFolders(_target.KitDir);
            HashSet<ToolSpec> selected = new(DefaultSelection(_target));
            if (_only != null)
            {
                selected = new HashSet<ToolSpec>(_only);
            }

            Rows = Catalog.All.Select(t => new ToolRowViewModel(t, selected.Contains(t), installed.Contains(t.Folder), OnSelectionChanged)).ToList();
            Groups = Rows.GroupBy(r => r.Spec.Category).Select(g => new ToolGroupRows(g.Key, g.ToList())).ToList();
            OnPropertyChanged(nameof(KitDir));
            OnPropertyChanged(nameof(TargetText));
            OnSelectionChanged();
        }
    }

    public string KitDir => _target.KitDir;

    public string TargetText
    {
        get
        {
            if (!_target.Existing)
            {
                return "Creates " + _target.KitDir;
            }

            string name = Path.GetFileName(_target.KitDir);
            string wanted = KitLayout.FolderName(SetupInfo.Version);
            return "Updates the kit in " + _target.KitDir +
                   (name.Equals(wanted, StringComparison.OrdinalIgnoreCase) ? "" : $" and renames it to {wanted}") +
                   ". Tools you added yourself and your reports are kept.";
        }
    }

    public List<ToolRowViewModel> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    public List<ToolGroupRows> Groups
    {
        get => _groups;
        private set => SetProperty(ref _groups, value);
    }

    public bool Redownload
    {
        get => _redownload;
        set => SetProperty(ref _redownload, value);
    }

    public string SizeText
    {
        get
        {
            List<ToolRowViewModel> chosen = Rows.Where(r => r.IsSelected).ToList();
            double mb = chosen.Sum(r => r.Spec.SizeMb) + 5; // DiagKit.exe and Uninstall.exe
            int removed = Rows.Count(r => !r.IsSelected && r.IsInstalled);
            return $"{chosen.Count} of {Rows.Count} tools · about {mb:0} MB" +
                   (removed > 0 ? $" · {removed} unticked {(removed == 1 ? "tool" : "tools")} will be removed from the kit" : "");
        }
    }

    public string Activity
    {
        get => _activity;
        private set => SetProperty(ref _activity, value);
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

    /// <summary>A new kit gets everything; an existing kit everything except the tools the user unticked before.</summary>
    public static IEnumerable<ToolSpec> DefaultSelection(KitTarget target)
    {
        if (!target.Existing)
        {
            return Catalog.All;
        }

        // Everything except what the user unticked last time: tools that failed to download are retried, and tools
        // added in a newer DiagKit are offered.
        var unwanted = new HashSet<string>(KitLedger.Load(target.KitDir).Unwanted, StringComparer.OrdinalIgnoreCase);
        return Catalog.All.Where(t => t.Required || !unwanted.Contains(t.Folder));
    }

    private void OnSelectionChanged() => OnPropertyChanged(nameof(SizeText));

    private void SetAll(bool on)
    {
        foreach (ToolRowViewModel r in Rows)
        {
            r.IsSelected = on;
        }
    }

    private void Browse()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose the USB stick (or folder) for the kit. A diagkit folder is created inside it; pick an existing kit to update it.",
            SelectedPath = Directory.Exists(Parent) ? Parent : "",
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            Parent = dialog.SelectedPath;
        }
    }

    private async Task RunAsync()
    {
        Stage = SetupStage.Working;
        foreach (ToolRowViewModel r in Rows)
        {
            r.Led = LedState.Off;
            r.Detail = r.IsSelected ? "waiting" : r.IsInstalled ? "will be removed" : "not included";
        }

        _cts = new CancellationTokenSource();
        var installer = new KitInstaller(_target, Rows.Where(r => r.IsSelected).Select(r => r.Spec), Redownload || _force, OnProgress, OnLog);
        try
        {
            InstallSummary s = await Task.Run(() => installer.RunAsync(_cts.Token));
            _target = new KitTarget(s.KitDir, true);
            OnPropertyChanged(nameof(KitDir));
            foreach (string note in s.Notes)
            {
                OnLog(note);
            }

            if (s.Failed == 0)
            {
                ResultLed = LedState.Ok;
                ResultTitle = "Your kit is ready";
                ResultText = $"Everything was downloaded and verified. Start DiagKit.exe in {s.KitDir}; run setup again any time to update the tools.";
            }
            else
            {
                ResultLed = LedState.Warning;
                ResultTitle = $"Ready, but {s.Failed} {(s.Failed == 1 ? "tool" : "tools")} could not be downloaded";
                ResultText = "DiagKit works without them. Every source was tried (see below); run setup again later, when the sites are back.";
            }
        }
        catch (OperationCanceledException)
        {
            ResultLed = LedState.Warning;
            ResultTitle = "Setup was cancelled";
            ResultText = "Tools that finished are in the kit; run setup again to complete it.";
        }
        catch (Exception ex)
        {
            ResultLed = LedState.Critical;
            ResultTitle = "Setup could not finish";
            ResultText = ex.Message;
        }

        Activity = "";
        Stage = SetupStage.Done;
    }

    private void OnProgress(ToolProgress p)
    {
        Dispatch(() =>
        {
            ToolRowViewModel? row = Rows.FirstOrDefault(r => r.Spec == p.Tool);
            if (row == null)
            {
                return;
            }

            row.Detail = p.Detail;
            row.Led = p.State switch
            {
                ToolState.Working => LedState.Busy,
                ToolState.Done => LedState.Ok,
                ToolState.Kept => LedState.Info,
                ToolState.Removed => LedState.Off,
                ToolState.Failed => LedState.Critical,
                _ => LedState.Off,
            };
            if (p.State == ToolState.Working)
            {
                Activity = $"{p.Tool.Name}: {p.Detail}";
            }
        });
    }

    private void OnLog(string line) => Dispatch(() => Log.Add(line));

    private static void Dispatch(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    private static void Start(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "DiagKit setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

public sealed class DriveChoice
{
    public DriveChoice(DriveInfo d)
    {
        Root = d.RootDirectory.FullName;
        string label = "";
        string free = "";
        try
        {
            label = d.VolumeLabel;
            free = $"{d.AvailableFreeSpace / 1e9:0.#} GB free";
        }
        catch
        {
            // Not ready.
        }

        Text = $"{Root.TrimEnd('\\')}  {(label.Length > 0 ? label : "USB drive")}";
        Free = free;
    }

    public string Root { get; }
    public string Text { get; }
    public string Free { get; }
}
