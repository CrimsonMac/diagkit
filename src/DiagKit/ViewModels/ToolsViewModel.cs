using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using DiagKit.Models;
using DiagKit.Services;

namespace DiagKit.ViewModels;

public sealed class ToolViewModel : ObservableObject
{
    private string? _exePath;
    private int _runningCount;
    private object? _icon;
    private bool _isVisible = true;

    public ToolViewModel(ToolDefinition definition, string? exePath, Action<ToolViewModel> launch, Action<ToolViewModel> openFolder)
    {
        Definition = definition;
        _exePath = exePath;
        LaunchCommand = new RelayCommand(() => launch(this));
        OpenFolderCommand = new RelayCommand(() => openFolder(this));
    }

    public ToolDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public string Category => Definition.Category;
    public ICommand LaunchCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public string? ExePath
    {
        get => _exePath;
        set
        {
            if (SetProperty(ref _exePath, value))
            {
                NotifyState();
            }
        }
    }

    public bool IsAvailable => _exePath != null;

    public int RunningCount
    {
        get => _runningCount;
        set
        {
            if (SetProperty(ref _runningCount, Math.Max(0, value)))
            {
                NotifyState();
            }
        }
    }

    public bool IsRunning => _runningCount > 0;

    public LedState Led => !IsAvailable ? LedState.Off : IsRunning ? LedState.Busy : LedState.Ok;

    public string StatusText => !IsAvailable ? "Not found" : IsRunning ? (_runningCount > 1 ? $"{_runningCount} running" : "Running") : "Ready";

    public string LocationText => IsAvailable
        ? Path.GetRelativePath(AppPaths.BaseDir, _exePath!)
        : $"Not found. Put {Definition.Executables.FirstOrDefault()} in tools\\{Definition.Folder}, then Rescan.";

    public object? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
            }
        }
    }

    public bool HasIcon => _icon != null;

    /// <summary>Two-letter monogram shown until (or instead of) the exe icon.</summary>
    public string Monogram
    {
        get
        {
            string letters = new string(Name.Where(char.IsLetterOrDigit).ToArray());
            return letters.Length >= 2 ? letters[..2].ToUpperInvariant() : letters.ToUpperInvariant();
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(Led));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LocationText));
    }

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        string q = query.Trim();
        return Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               Category.Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ToolGroupViewModel : ObservableObject
{
    private bool _isVisible = true;

    public ToolGroupViewModel(string name, List<ToolViewModel> tools)
    {
        Name = name;
        Tools = tools;
    }

    public string Name { get; }
    public List<ToolViewModel> Tools { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }
}

public sealed class ToolsViewModel : ObservableObject
{
    private static readonly ConcurrentDictionary<string, object?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, object?> _iconLoader;
    private readonly Action<string, LedState> _notify;
    private readonly ConcurrentDictionary<string, int> _running = new();
    private List<ToolViewModel> _all = new();
    private string _searchText = "";
    private bool _isLoading = true;
    private string _summary = "Looking for tools";
    private bool _hasNoMatches;
    private Task? _loadTask;

    public ToolsViewModel(Func<string, object?> iconLoader, Action<string, LedState> notify)
    {
        _iconLoader = iconLoader;
        _notify = notify;
        RescanCommand = new AsyncCommand(LoadAsync);
        OpenToolsFolderCommand = new RelayCommand(() => ShellOpen.Open(AppPaths.ToolsRoot));
        LaunchFirstCommand = new RelayCommand(LaunchFirstMatch);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
    }

    public ObservableCollection<ToolGroupViewModel> Groups { get; } = new();
    public ICommand RescanCommand { get; }
    public ICommand OpenToolsFolderCommand { get; }
    public ICommand LaunchFirstCommand { get; }
    public ICommand ClearSearchCommand { get; }

    public event Action? FocusSearchRequested;

    public Task LoadTask => _loadTask ?? Task.CompletedTask;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? ""))
            {
                ApplyFilter();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public bool HasNoMatches
    {
        get => _hasNoMatches;
        private set => SetProperty(ref _hasNoMatches, value);
    }

    public IEnumerable<ToolViewModel> RunningTools => _all.Where(t => t.IsRunning);

    private bool _focusPending;

    /// <summary>Asks the view to focus search; remembered if the page is still being created.</summary>
    public void RequestSearchFocus()
    {
        if (FocusSearchRequested != null)
        {
            FocusSearchRequested();
        }
        else
        {
            _focusPending = true;
        }
    }

    public bool TakeFocusRequest()
    {
        bool pending = _focusPending;
        _focusPending = false;
        return pending;
    }

    public IReadOnlyDictionary<string, string?> ToolPaths() =>
        _all.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().ExePath, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ToolDefinition> ToolDefinitions() =>
        _all.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Definition, StringComparer.OrdinalIgnoreCase);

    public Task LoadAsync()
    {
        _loadTask = LoadCoreAsync();
        return _loadTask;
    }

    private async Task LoadCoreAsync()
    {
        IsLoading = true;

        // Disk work (manifest parse, recursive exe search) stays off the UI thread; on a slow USB stick this is the costly part.
        ManifestLoadResult result;
        List<(ToolDefinition Def, string? Path)> resolved;
        try
        {
            (result, resolved) = await Task.Run(() =>
            {
                ManifestLoadResult r = ToolCatalog.LoadManifest();
                List<string?> found = ToolCatalog.ResolveAll(r.Manifest.Tools);
                var list = r.Manifest.Tools.Select((t, i) => (Def: t, Path: found[i])).ToList();
                return (r, list);
            });
        }
        catch (Exception ex)
        {
            AppPaths.LogError("Loading tools", ex);
            IsLoading = false;
            Summary = "Could not read the tools folder";
            _notify("Could not read the tools folder: " + ex.Message, LedState.Critical);
            return;
        }

        _all = resolved.Select(x => new ToolViewModel(x.Def, x.Path, Launch, OpenFolder)
        {
            RunningCount = _running.TryGetValue(x.Def.Id, out int n) ? n : 0,
            Icon = x.Path != null && IconCache.TryGetValue(x.Path, out object? cached) ? cached : null,
        }).ToList();

        Groups.Clear();
        foreach (IGrouping<string, ToolViewModel> g in _all.Where(t => !t.Definition.Hidden).GroupBy(t => t.Category))
        {
            Groups.Add(new ToolGroupViewModel(g.Key, g.ToList()));
        }

        ApplyFilter();
        int visible = _all.Count(t => !t.Definition.Hidden);
        int ready = _all.Count(t => !t.Definition.Hidden && t.IsAvailable);
        Summary = ready == visible
            ? $"All {visible} tools ready"
            : $"{ready} of {visible} tools ready. Missing ones show where their files go.";
        IsLoading = false;

        if (result.Warning != null)
        {
            _notify(result.Warning, LedState.Warning);
        }

        List<ToolViewModel> needIcons = _all.Where(t => t.ExePath != null && t.Icon == null).ToList();
        if (needIcons.Count > 0)
        {
            _ = Task.Run(() =>
            {
                foreach (ToolViewModel t in needIcons)
                {
                    object? icon = IconCache.GetOrAdd(t.ExePath!, p => _iconLoader(p));
                    t.Icon = icon;
                }
            });
        }
    }

    private void ApplyFilter()
    {
        bool any = false;
        foreach (ToolGroupViewModel g in Groups)
        {
            bool groupVisible = false;
            foreach (ToolViewModel t in g.Tools)
            {
                t.IsVisible = t.Matches(_searchText);
                groupVisible |= t.IsVisible;
            }

            g.IsVisible = groupVisible;
            any |= groupVisible;
        }

        HasNoMatches = !any && !IsLoading && Groups.Count > 0;
    }

    private void LaunchFirstMatch()
    {
        ToolViewModel? first = Groups.SelectMany(g => g.Tools).FirstOrDefault(t => t.IsVisible && t.IsAvailable);
        if (first != null)
        {
            Launch(first);
        }
    }

    private void ChangeRunning(string id, int delta)
    {
        int now = _running.AddOrUpdate(id, Math.Max(0, delta), (_, v) => Math.Max(0, v + delta));
        foreach (ToolViewModel t in _all.Where(t => t.Id == id))
        {
            t.RunningCount = now;
        }
    }

    private async void Launch(ToolViewModel tool)
    {
        if (!tool.IsAvailable || tool.ExePath == null)
        {
            OpenFolder(tool);
            _notify($"{tool.Name} isn't in the kit yet. Put {tool.Definition.Executables.FirstOrDefault()} in the folder that just opened, then press Rescan.", LedState.Info);
            return;
        }

        if (!File.Exists(tool.ExePath))
        {
            _notify($"{tool.Name} is no longer at {tool.LocationText}. It may have been quarantined. Press Rescan.", LedState.Warning);
            return;
        }

        LaunchOutcome outcome = ToolLauncher.Start(tool.Definition, tool.ExePath);
        if (!outcome.Started)
        {
            _notify(outcome.Error ?? $"{tool.Name} could not start.", LedState.Critical);
            return;
        }

        ChangeRunning(tool.Id, +1);
        try
        {
            string? early = await outcome.EarlyFailure;
            if (early != null)
            {
                _notify(early, LedState.Warning);
            }

            await outcome.Completion;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("Watching " + tool.Name, ex);
        }
        finally
        {
            ChangeRunning(tool.Id, -1);
        }
    }

    private void OpenFolder(ToolViewModel tool)
    {
        string dir = tool.ExePath != null
            ? Path.GetDirectoryName(tool.ExePath) ?? AppPaths.ToolsRoot
            : Path.Combine(AppPaths.ToolsRoot, tool.Definition.Folder);
        if (!Directory.Exists(dir))
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                dir = AppPaths.ToolsRoot;
            }
        }

        ShellOpen.Open(dir);
    }
}
