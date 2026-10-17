using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using DiagKit.Kit;
using DiagKit.ViewModels;

namespace DiagKit.Setup;

/// <summary>A drive in the picker's side list.</summary>
public sealed class PlaceItem
{
    public PlaceItem(DriveInfo d)
    {
        Root = d.RootDirectory.FullName;
        string label = "";
        try
        {
            label = d.VolumeLabel;
            Free = $"{Size(d.AvailableFreeSpace)} free of {Size(d.TotalSize)}";
        }
        catch
        {
            Free = "";
        }

        (Glyph, string kind) = d.DriveType switch
        {
            DriveType.Removable => ("", "USB drive"),
            DriveType.Network => ("", "Network drive"),
            _ => ("", "Local disk"),
        };
        IsRemovable = d.DriveType == DriveType.Removable;
        Name = $"{(label.Length > 0 ? label : kind)} ({Root.TrimEnd('\\')})";
    }

    public string Root { get; }
    public string Name { get; }
    public string Free { get; }
    public string Glyph { get; }
    public bool IsRemovable { get; }

    private static string Size(long bytes) => bytes >= 1e12 ? $"{bytes / 1e12:0.#} TB" : bytes >= 1e10 ? $"{bytes / 1e9:0} GB" : $"{bytes / 1e9:0.#} GB";
}

/// <summary>A sub-folder of the folder being browsed.</summary>
public sealed class FolderItem
{
    public FolderItem(string path)
    {
        FullPath = path;
        Name = Path.GetFileName(path);
        IsKit = KitLayout.IsKit(path);
    }

    public string FullPath { get; }
    public string Name { get; }
    public bool IsKit { get; }
    public string Glyph => IsKit ? "" : "";
}

/// <summary>
/// Setup's own folder browser. It shows where the kit will actually end up before the user commits, because the
/// picked folder is only the parent: a diagkit.&lt;version&gt; folder is created inside it (always so at a drive's
/// root, so a kit never spills its files across a USB stick), unless the folder already is or holds a kit.
/// </summary>
public sealed class FolderPickerViewModel : ObservableObject
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly RelayCommand _upCommand;
    private readonly RelayCommand _createCommand;
    private readonly string _start;
    private string _currentPath = "";
    private string _pathText = "";
    private List<FolderItem> _folders = new();
    private FolderItem? _selected;
    private PlaceItem? _selectedPlace;
    private string _error = "";
    private bool _isCreating;
    private string _newFolderName = "";
    private string _newFolderError = "";
    private int _loadVersion;
    private bool _started;
    private bool _syncingPlace;

    public FolderPickerViewModel(string start)
    {
        _start = start;
        Places = DriveInfo.GetDrives().Where(IsUsable).Select(d => new PlaceItem(d))
            .OrderByDescending(p => p.IsRemovable).ThenBy(p => p.Root, StringComparer.OrdinalIgnoreCase).ToList();
        _upCommand = new RelayCommand(() => Navigate(Parent(_currentPath)!), () => Parent(_currentPath) != null);
        _createCommand = new RelayCommand(CreateFolder, () => _isCreating);
        GoCommand = new RelayCommand(() => Navigate(PathText.Trim()));
        OpenCommand = new RelayCommand(p =>
        {
            if ((p as FolderItem ?? _selected) is { } f)
            {
                Navigate(f.FullPath);
            }
        });
        NewFolderCommand = new RelayCommand(() =>
        {
            NewFolderName = UniqueName("New folder");
            NewFolderError = "";
            IsCreating = true;
        });
        CancelCreateCommand = new RelayCommand(() => IsCreating = false);
    }

    /// <summary>
    /// Opens the start folder. Called once the window has loaded, so the first listing is not overtaken by a load
    /// that the bindings start while the window is still being set up.
    /// </summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        Navigate(Directory.Exists(_start) ? _start : Places.FirstOrDefault()?.Root ?? Path.GetPathRoot(Environment.SystemDirectory)!);
    }

    public ICommand UpCommand => _upCommand;
    public ICommand GoCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand NewFolderCommand { get; }
    public ICommand CreateCommand => _createCommand;
    public ICommand CancelCreateCommand { get; }

    public List<PlaceItem> Places { get; }

    /// <summary>The folder whose contents are listed.</summary>
    public string CurrentPath
    {
        get => _currentPath;
        private set => SetProperty(ref _currentPath, value);
    }

    /// <summary>The editable path box; Enter navigates to it.</summary>
    public string PathText
    {
        get => _pathText;
        set => SetProperty(ref _pathText, value);
    }

    public List<FolderItem> Folders
    {
        get => _folders;
        private set
        {
            if (SetProperty(ref _folders, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool IsEmpty => _folders.Count == 0 && _error.Length == 0;

    /// <summary>A highlighted sub-folder; when set, it (not the folder being listed) is what gets picked.</summary>
    public FolderItem? SelectedFolder
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnChosenChanged();
            }
        }
    }

    /// <summary>The drive holding the current folder; only the user picking a drive navigates to it.</summary>
    public PlaceItem? SelectedPlace
    {
        get => _selectedPlace;
        set
        {
            // The list's two-way binding writes back while the window is set up and when Navigate moves the
            // highlight; those writes must not start a load that makes the real one stale.
            if (SetProperty(ref _selectedPlace, value) && _started && !_syncingPlace && value != null && !IsSameFolder(value.Root, _currentPath))
            {
                Navigate(value.Root);
            }
        }
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool IsCreating
    {
        get => _isCreating;
        private set
        {
            if (SetProperty(ref _isCreating, value))
            {
                _createCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string NewFolderName
    {
        get => _newFolderName;
        set
        {
            if (SetProperty(ref _newFolderName, value))
            {
                NewFolderError = "";
            }
        }
    }

    public string NewFolderError
    {
        get => _newFolderError;
        private set => SetProperty(ref _newFolderError, value);
    }

    /// <summary>The folder that "Use this folder" hands back to setup.</summary>
    public string Chosen => _selected?.FullPath ?? _currentPath;

    public bool CanChoose => Directory.Exists(Chosen);

    public string ChosenText => "Selected: " + Chosen;

    /// <summary>Where the kit will actually go, in words.</summary>
    public string PreviewText
    {
        get
        {
            KitTarget target;
            try
            {
                target = KitTarget.For(Chosen);
            }
            catch
            {
                return "";
            }

            if (target.Existing)
            {
                return $"There is already a kit here, so setup updates it: {target.KitDir}";
            }

            string parent = Chosen.TrimEnd('\\');
            return parent.Length == 2 && parent[1] == ':'
                ? $"This is the top of the drive, so the kit gets a folder of its own instead of spreading files across it: {target.KitDir}"
                : $"The kit goes in a new folder inside it: {target.KitDir}";
        }
    }

    public bool PreviewIsUpdate
    {
        get
        {
            try
            {
                return KitTarget.For(Chosen).Existing;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Navigate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string full;
        try
        {
            full = Locations.FullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch (Exception ex)
        {
            Error = "That is not a valid folder path: " + ex.Message;
            return;
        }

        if (!Directory.Exists(full))
        {
            Error = $"{full} does not exist. Check the path, or go to its parent and create it with New folder.";
            PathText = _currentPath;
            return;
        }

        IsCreating = false;
        CurrentPath = full;
        PathText = full;
        _selectedPlace = Places.FirstOrDefault(p => full.StartsWith(p.Root, StringComparison.OrdinalIgnoreCase));
        _syncingPlace = true;
        try
        {
            OnPropertyChanged(nameof(SelectedPlace));
        }
        finally
        {
            _syncingPlace = false;
        }

        _upCommand.RaiseCanExecuteChanged();
        _ = LoadAsync(full, null);
    }

    private async Task LoadAsync(string path, string? select)
    {
        int version = ++_loadVersion;
        _selected = null;
        OnChosenChanged();
        OnPropertyChanged(nameof(SelectedFolder));

        // A slow network share or a waking USB disk must not freeze the window.
        (List<FolderItem> items, string error) = await Task.Run(() => List(path));
        if (version != _loadVersion)
        {
            return;
        }

        Error = error;
        Folders = items;
        if (select != null)
        {
            SelectedFolder = items.FirstOrDefault(f => IsSameFolder(f.FullPath, select));
        }
    }

    private static (List<FolderItem>, string) List(string path)
    {
        try
        {
            var items = new List<FolderItem>();
            foreach (string dir in Directory.EnumerateDirectories(path))
            {
                try
                {
                    // Hidden and system folders ($RECYCLE.BIN, System Volume Information) are no place for a kit.
                    if ((File.GetAttributes(dir) & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                items.Add(new FolderItem(dir));
            }

            return (items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), "");
        }
        catch (UnauthorizedAccessException)
        {
            return (new List<FolderItem>(), "Windows does not allow listing this folder. Pick another one.");
        }
        catch (Exception ex)
        {
            return (new List<FolderItem>(), "This folder could not be read: " + ex.Message);
        }
    }

    private void CreateFolder()
    {
        string name = NewFolderName.Trim();
        if (name.Length == 0)
        {
            NewFolderError = "Type a name for the folder.";
            return;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Trim('.').Length == 0 || name.EndsWith("."))
        {
            NewFolderError = "A folder name cannot contain \\ / : * ? \" < > | or end with a dot.";
            return;
        }

        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name)))
        {
            NewFolderError = $"Windows reserves the name {name}. Pick another.";
            return;
        }

        string path = Path.Combine(_currentPath, name);
        if (Directory.Exists(path) || File.Exists(path))
        {
            NewFolderError = $"There is already something called {name} here.";
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (UnauthorizedAccessException)
        {
            NewFolderError = "Windows does not allow creating a folder here. Pick a USB stick or a folder you own.";
            return;
        }
        catch (Exception ex)
        {
            NewFolderError = "The folder could not be created: " + ex.Message;
            return;
        }

        IsCreating = false;
        _ = LoadAsync(_currentPath, path);
    }

    private string UniqueName(string baseName)
    {
        string name = baseName;
        for (int n = 2; Directory.Exists(Path.Combine(_currentPath, name)) || File.Exists(Path.Combine(_currentPath, name)); n++)
        {
            name = $"{baseName} ({n})";
        }

        return name;
    }

    private void OnChosenChanged()
    {
        OnPropertyChanged(nameof(Chosen));
        OnPropertyChanged(nameof(ChosenText));
        OnPropertyChanged(nameof(CanChoose));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(PreviewIsUpdate));
    }

    private static string? Parent(string path)
    {
        try
        {
            return string.IsNullOrEmpty(path) ? null : Directory.GetParent(path.TrimEnd('\\') + (path.Length <= 3 ? "\\" : ""))?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSameFolder(string a, string b)
        => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool IsUsable(DriveInfo d)
    {
        try
        {
            return d.IsReady && d.DriveType is DriveType.Removable or DriveType.Fixed or DriveType.Network;
        }
        catch
        {
            return false;
        }
    }
}
