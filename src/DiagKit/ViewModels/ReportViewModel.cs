using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using DiagKit.Models;
using DiagKit.Report;
using DiagKit.Services;

namespace DiagKit.ViewModels;

public static class LedMap
{
    public static LedState From(Severity s) => s switch
    {
        Severity.Critical => LedState.Critical,
        Severity.Warning => LedState.Warning,
        Severity.Info => LedState.Info,
        _ => LedState.Ok,
    };
}

public sealed class FindingViewModel : ObservableObject
{
    private bool _isExpanded;

    public FindingViewModel(Finding finding)
    {
        Finding = finding;
        _isExpanded = finding.Severity >= Severity.Warning;
    }

    public Finding Finding { get; }
    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public string? Action => Finding.Action;
    public bool HasAction => !string.IsNullOrWhiteSpace(Finding.Action);
    public string Category => Finding.Category;
    public List<string> Evidence => Finding.Evidence;
    public bool HasEvidence => Finding.Evidence.Count > 0;
    public Severity Severity => Finding.Severity;
    public LedState Led => LedMap.From(Finding.Severity);
    public bool IsUrgent => Finding.Severity >= Severity.Warning;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
}

public sealed record LedRow(LedState Led, string Name, string Detail, string Extra);

public sealed record TallyItem(LedState Led, int Count, string Label);

public sealed record DriveRowViewModel(DriveRow Row, LedState Led);

public sealed record ReadingViewModel(Reading Reading, LedState Led);

public sealed class ReportViewModel : ObservableObject
{
    public ReportViewModel(DiagnosticReport report, string folder, string? htmlPath, Action<string, LedState> notify, bool fromHistory = false)
    {
        Report = report;
        Folder = folder;
        HtmlPath = htmlPath ?? (File.Exists(Path.Combine(folder, ReportStore.HtmlFile)) ? Path.Combine(folder, ReportStore.HtmlFile) : null);
        FromHistory = fromHistory;

        Verdict = ReportFacts.Verdict(report);
        VerdictDetail = ReportFacts.VerdictDetail(report);
        VerdictLed = LedMap.From(ReportFacts.Worst(report));
        MachineLine = $"{report.MachineName}   {ReportFacts.MachineTitle(report)}";
        StampText = $"Checked {TextUtil.DateTimeShort(report.CreatedAt)} in {report.DurationSeconds:0} s" +
                    (fromHistory ? $" ({TextUtil.Ago(report.CreatedAt)})" : "");

        Tally = new List<TallyItem>
        {
            new(LedState.Critical, ReportFacts.Count(report, Severity.Critical), "critical"),
            new(LedState.Warning, ReportFacts.Count(report, Severity.Warning), "worth fixing"),
            new(LedState.Info, ReportFacts.Count(report, Severity.Info), "good to know"),
            new(LedState.Ok, ReportFacts.Count(report, Severity.Ok), "fine"),
        }.Select(t => t.Count == 0 ? t with { Led = LedState.Off } : t).ToList();

        Findings = report.Findings.Select(f => new FindingViewModel(f)).ToList();
        Overview = ReportFacts.Overview(report);
        Drives = ReportFacts.Drives(report).Select(d => new DriveRowViewModel(d, LedMap.From(d.Severity))).ToList();
        Readings = ReportFacts.Readings(report).Select(r => new ReadingViewModel(r, LedMap.From(r.Severity))).ToList();
        Collectors = report.Collectors.Select(c => new LedRow(
            c.Status switch { "ok" => LedState.Ok, "partial" => LedState.Warning, _ => LedState.Critical },
            c.Name, c.Message ?? "", $"{c.Seconds:0.0} s")).ToList();

        OpenHtmlCommand = new RelayCommand(() =>
        {
            if (HtmlPath == null || !ShellOpen.Open(HtmlPath, out string? error))
            {
                notify("Could not open the HTML report" + (HtmlPath == null ? ": it was not saved." : "."), LedState.Warning);
            }
        }, () => HtmlPath != null);
        OpenFolderCommand = new RelayCommand(() => ShellOpen.Open(Folder));
        ExpandAllCommand = new RelayCommand(() =>
        {
            bool expand = Findings.Any(f => !f.IsExpanded);
            foreach (FindingViewModel f in Findings)
            {
                f.IsExpanded = expand;
            }
        });
    }

    public DiagnosticReport Report { get; }
    public string Folder { get; }
    public string? HtmlPath { get; }
    public bool FromHistory { get; }
    public string Verdict { get; }
    public string VerdictDetail { get; }
    public LedState VerdictLed { get; }
    public string MachineLine { get; }
    public string StampText { get; }
    public List<TallyItem> Tally { get; }
    public List<FindingViewModel> Findings { get; }
    public List<Fact> Overview { get; }
    public List<DriveRowViewModel> Drives { get; }
    public List<ReadingViewModel> Readings { get; }
    public List<LedRow> Collectors { get; }
    public bool HasDrives => Drives.Count > 0;
    public bool HasReadings => Readings.Count > 0;
    public bool HasFindings => Findings.Count > 0;
    public ICommand OpenHtmlCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ExpandAllCommand { get; }
}
