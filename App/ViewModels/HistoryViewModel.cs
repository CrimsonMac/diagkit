using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using DiagKit.Models;
using DiagKit.Report;
using DiagKit.Services;

namespace DiagKit.ViewModels;

public sealed class ReportListingViewModel
{
    public ReportListingViewModel(ReportListing listing, HistoryViewModel owner)
    {
        Listing = listing;
        OpenCommand = new AsyncCommand(() => owner.OpenAsync(this));
        OpenHtmlCommand = new RelayCommand(() => ShellOpen.Open(listing.HtmlPath!), () => listing.HtmlPath != null);
        OpenFolderCommand = new RelayCommand(() => ShellOpen.Open(listing.Folder));
        DeleteCommand = new RelayCommand(() => owner.Delete(this));
    }

    public ReportListing Listing { get; }
    public string Machine => Listing.MachineName;
    public string When => TextUtil.DateTimeShort(Listing.CreatedAt);
    public string Ago => TextUtil.Ago(Listing.CreatedAt);

    public LedState Led => Listing.Critical > 0 ? LedState.Critical
        : Listing.Warning > 0 ? LedState.Warning
        : LedState.Ok;

    public string Summary => (Listing.Scope != null ? Listing.Scope + " · " : "") + Verdict;

    private string Verdict => Listing.Critical > 0
        ? $"{TextUtil.Plural(Listing.Critical, "critical problem")}" + (Listing.Warning > 0 ? $", {Listing.Warning} worth fixing" : "")
        : Listing.Warning > 0
            ? $"{Listing.Warning} worth fixing"
            : "Nothing needed attention";

    public ICommand OpenCommand { get; }
    public ICommand OpenHtmlCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand DeleteCommand { get; }
}

public sealed class HistoryViewModel : ObservableObject
{
    private readonly Action<ReportViewModel> _open;
    private readonly Action<string, LedState> _notify;
    private readonly Func<string, bool> _confirm;
    private bool _isLoading;
    private bool _loadedOnce;

    public HistoryViewModel(Action<ReportViewModel> open, Action<string, LedState> notify, Func<string, bool> confirm)
    {
        _open = open;
        _notify = notify;
        _confirm = confirm;
        RefreshCommand = new AsyncCommand(LoadAsync);
        OpenReportsFolderCommand = new RelayCommand(() => ShellOpen.Open(AppPaths.ReportsRoot));
    }

    public ObservableCollection<ReportListingViewModel> Items { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand OpenReportsFolderCommand { get; }

    public string LocationText => AppPaths.ReportsAreOnKit
        ? "Saved in the kit's reports folder, so they travel with the USB stick."
        : $"Saved in {AppPaths.ReportsRoot} because the kit folder is read-only.";

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool IsEmpty => !_isLoading && Items.Count == 0;

    public Task EnsureLoadedAsync() => _loadedOnce ? Task.CompletedTask : LoadAsync();

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var listings = await Task.Run(ReportStore.List);
            Items.Clear();
            foreach (ReportListing l in listings)
            {
                Items.Add(new ReportListingViewModel(l, this));
            }

            _loadedOnce = true;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("Listing reports", ex);
            _notify("Could not read the reports folder: " + ex.Message, LedState.Warning);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    internal async Task OpenAsync(ReportListingViewModel item)
    {
        DiagnosticReport? report;
        try
        {
            report = await Task.Run(() => ReportStore.Load(item.Listing.Folder));
        }
        catch (Exception ex)
        {
            AppPaths.LogError("Opening report in " + item.Listing.Folder, ex);
            report = null;
        }

        if (report == null)
        {
            _notify("That report is missing or damaged.", LedState.Warning);
            return;
        }

        _open(new ReportViewModel(report, item.Listing.Folder, item.Listing.HtmlPath, _notify, fromHistory: true));
    }

    internal void Delete(ReportListingViewModel item)
    {
        if (!_confirm($"Delete the report for {item.Machine} from {item.When}? This removes its folder, including raw data."))
        {
            return;
        }

        try
        {
            Directory.Delete(item.Listing.Folder, recursive: true);
            Items.Remove(item);
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex)
        {
            _notify("Could not delete the report: " + ex.Message, LedState.Warning);
        }
    }
}
