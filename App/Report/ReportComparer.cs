using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DiagKit.Services;

namespace DiagKit.Report;

/// <summary>
/// Compares a new report with the previous check of the same PC, so a second visit (or a check after a repair) shows
/// what appeared, what got worse and what went away, and whether a drive's error counters moved.
/// </summary>
public static class ReportComparer
{
    // Numbers in titles change between checks ("Not restarted in 20 days"); without them a title names the same problem.
    private static readonly Regex Numbers = new(@"\d[\d,.]*");

    /// <summary>
    /// The newest earlier report of this PC with the same scope (a full check against a full check, a quick check against
    /// the same quick check), or null. A different PC that happens to share the name is skipped by its model.
    /// </summary>
    public static (DiagnosticReport Report, string Folder)? FindPrevious(DiagnosticReport current, string currentFolder)
    {
        IEnumerable<ReportListing> candidates = ReportStore.List()
            .Where(l => string.Equals(l.MachineName, current.MachineName, StringComparison.OrdinalIgnoreCase) &&
                        l.Scope == current.Scope && l.CreatedAt < current.CreatedAt &&
                        !string.Equals(Path.GetFullPath(l.Folder), Path.GetFullPath(currentFolder), StringComparison.OrdinalIgnoreCase));

        // ReportStore.List is newest first; only the first few are worth loading in full.
        foreach (ReportListing listing in candidates.Take(5))
        {
            DiagnosticReport? previous = ReportStore.Load(listing.Folder);
            if (previous != null && SamePc(previous.System, current.System))
            {
                return (previous, listing.Folder);
            }
        }

        return null;
    }

    private static bool SamePc(SystemInfo a, SystemInfo b)
    {
        static bool Same(string? x, string? y) => x == null || y == null || string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        return Same(a.Model, b.Model) && Same(a.Motherboard, b.Motherboard);
    }

    /// <summary>Fills in <see cref="DiagnosticReport.Comparison"/> for a report that is about to be saved.</summary>
    public static void Attach(DiagnosticReport current, string currentFolder)
    {
        try
        {
            if (FindPrevious(current, currentFolder) is { } previous)
            {
                current.Comparison = Compare(previous.Report, current, Path.GetFileName(previous.Folder));
            }
        }
        catch (Exception ex)
        {
            // A comparison is a bonus; an unreadable old report must not cost the new one.
            AppPaths.LogError("Comparing with the previous report failed", ex);
        }
    }

    public static ReportComparison Compare(DiagnosticReport previous, DiagnosticReport current, string previousFolder)
    {
        var result = new ReportComparison { PreviousCreatedAt = previous.CreatedAt, PreviousFolder = previousFolder };
        List<ReportChange> changes = result.Changes;

        Dictionary<string, Finding> before = ByKey(previous.Findings);
        Dictionary<string, Finding> now = ByKey(current.Findings);

        foreach (KeyValuePair<string, Finding> pair in now)
        {
            Finding f = pair.Value;
            if (!before.TryGetValue(pair.Key, out Finding? old))
            {
                if (f.Severity > Severity.Ok)
                {
                    changes.Add(new ReportChange { Kind = ChangeKind.New, Severity = f.Severity, Text = f.Title });
                }
            }
            else if (f.Severity != old.Severity)
            {
                changes.Add(new ReportChange
                {
                    Kind = f.Severity > old.Severity ? ChangeKind.Worse : ChangeKind.Better,
                    Severity = f.Severity,
                    Text = $"{f.Title}: now {Label(f.Severity)}, was {Label(old.Severity)}",
                });
            }
        }

        // A step that failed this time says nothing about whether its old findings went away.
        if (ReportFacts.FailedSteps(current) > 0)
        {
            result.Note = "Some steps did not finish this time, so problems from last time that are missing now may still be there.";
        }
        else
        {
            foreach (KeyValuePair<string, Finding> pair in before)
            {
                if (pair.Value.Severity > Severity.Ok && !now.ContainsKey(pair.Key))
                {
                    changes.Add(new ReportChange { Kind = ChangeKind.Gone, Severity = Severity.Ok, Text = pair.Value.Title });
                }
            }
        }

        DiskCounters(previous, current, changes);
        BatteryWear(previous, current, changes);

        result.Changes = changes.OrderBy(c => c.Kind).ThenByDescending(c => c.Severity).ToList();
        return result;
    }

    private static Dictionary<string, Finding> ByKey(List<Finding> findings)
    {
        var map = new Dictionary<string, Finding>(StringComparer.OrdinalIgnoreCase);
        foreach (Finding f in findings)
        {
            string key = f.Id + "|" + Numbers.Replace(f.Title, "#");
            if (!map.ContainsKey(key))
            {
                map[key] = f;
            }
        }

        return map;
    }

    private static string Label(Severity s) => s switch
    {
        Severity.Critical => "critical",
        Severity.Warning => "worth fixing",
        Severity.Info => "good to know",
        _ => "fine",
    };

    private static void DiskCounters(DiagnosticReport previous, DiagnosticReport current, List<ReportChange> changes)
    {
        foreach (DiskInfo d in current.Disks)
        {
            DiskInfo? old = previous.Disks.FirstOrDefault(p => !string.IsNullOrEmpty(d.Serial)
                ? string.Equals(p.Serial, d.Serial, StringComparison.OrdinalIgnoreCase)
                : p.Model == d.Model && p.SizeBytes == d.SizeBytes);
            if (old == null)
            {
                continue;
            }

            void Rose(long? was, long? isNow, string what, Severity severity)
            {
                if (was is long a && isNow is long b && b > a)
                {
                    changes.Add(new ReportChange { Kind = ChangeKind.Counter, Severity = severity, Text = $"{d.Model}: {what} went from {a:N0} to {b:N0}" });
                }
            }

            Rose(old.PendingSectors, d.PendingSectors, "pending sectors", Severity.Critical);
            Rose(old.UncorrectableSectors, d.UncorrectableSectors, "uncorrectable sectors", Severity.Critical);
            Rose(old.ReallocatedSectors, d.ReallocatedSectors, "reallocated sectors", Severity.Warning);
            Rose(old.ReportedUncorrectable, d.ReportedUncorrectable, "reported uncorrectable errors", Severity.Warning);
            Rose(old.MediaErrors, d.MediaErrors, "media and data integrity errors", Severity.Warning);
            Rose(old.CrcErrors, d.CrcErrors, "connection (CRC) errors", Severity.Info);
            Rose(old.PercentageUsed, d.PercentageUsed, "rated write endurance used (%)", Severity.Info);
            Rose(old.UnsafeShutdowns, d.UnsafeShutdowns, "unsafe shutdowns", Severity.Info);

            if (old.LifeLeftPercent is int lifeWas && d.LifeLeftPercent is int lifeNow && lifeNow < lifeWas)
            {
                changes.Add(new ReportChange { Kind = ChangeKind.Counter, Severity = Severity.Info, Text = $"{d.Model}: remaining life went from {lifeWas}% to {lifeNow}%" });
            }
        }
    }

    private static void BatteryWear(DiagnosticReport previous, DiagnosticReport current, List<ReportChange> changes)
    {
        if (previous.Battery is { DesignCapacity: > 0 } was && current.Battery is { DesignCapacity: > 0 } now && now.WearPercent >= was.WearPercent + 2)
        {
            changes.Add(new ReportChange
            {
                Kind = ChangeKind.Counter, Severity = Severity.Info,
                Text = $"Battery: holds {100 - now.WearPercent:0}% of its original capacity, down from {100 - was.WearPercent:0}%",
            });
        }
    }

    /// <summary>Headline for the comparison section.</summary>
    public static string Summary(ReportComparison c)
    {
        string when = TextUtil.DateTimeShort(c.PreviousCreatedAt);
        if (c.Changes.Count == 0)
        {
            return $"Nothing changed since the previous check on {when}.";
        }

        return $"Compared with the previous check on {when}: " + string.Join(", ", new[]
        {
            Part(c, ChangeKind.New, "new"),
            Part(c, ChangeKind.Worse, "worse"),
            Part(c, ChangeKind.Counter, "changed reading", "changed readings"),
            Part(c, ChangeKind.Better, "better"),
            Part(c, ChangeKind.Gone, "gone"),
        }.Where(p => p != null)) + ".";
    }

    private static string? Part(ReportComparison c, ChangeKind kind, string word, string? plural = null)
    {
        int n = c.Changes.Count(x => x.Kind == kind);
        return n == 0 ? null : plural == null ? $"{n} {word}" : TextUtil.Plural(n, word, plural);
    }

    public static string KindLabel(ChangeKind kind) => kind switch
    {
        ChangeKind.New => "New",
        ChangeKind.Worse => "Worse",
        ChangeKind.Counter => "Changed",
        ChangeKind.Better => "Better",
        _ => "Gone",
    };
}
