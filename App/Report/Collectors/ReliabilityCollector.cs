using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DiagKit.Report.Collectors;

/// <summary>
/// Reads Reliability Monitor's history through WMI. Crashes and blue screens are left to the event-log step, which
/// reads them from their source; this step adds what that one does not look at: programs that stopped responding,
/// Windows and Store updates that failed, MSI installs that failed, and the stability index.
/// </summary>
public sealed class ReliabilityCollector : ICollector
{
    public string Name => "Reliability history";
    public string Description => "Programs that hang, failed updates and installs, Windows' stability index";
    public int Phase => 1;
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    private const string UpdateSource = "Microsoft-Windows-WindowsUpdateClient";

    // Store packages are named "<12-character product ID>-<package name>", e.g. 9NKSQGP7F2NH-5319275A.WhatsAppDesktop.
    private static readonly Regex StorePackage = new(@"^[0-9A-Z]{12}-(?<name>.+)$");

    public Task CollectAsync(CollectorContext ctx, CollectorStatus status, CancellationToken ct)
    {
        int days = EventLogCollector.Days;
        var summary = new ReliabilitySummary { WindowDays = days };
        string since = ManagementDateTimeConverter.ToDmtfDateTime(DateTime.Now.AddDays(-days));

        // Per update title: when it last failed (and with which code) and when it last installed.
        var updateFailures = new Dictionary<string, (int Count, DateTime? Last, string? Code)>(StringComparer.OrdinalIgnoreCase);
        var updateSuccess = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        List<Dictionary<string, object?>> records = Wmi.Query(
            "SELECT SourceName, EventIdentifier, ProductName, TimeGenerated, InsertionStrings FROM Win32_ReliabilityRecords " +
            $"WHERE TimeGenerated >= '{since}' AND (SourceName = 'Application Hang' OR SourceName = '{UpdateSource}' OR SourceName = 'MsiInstaller')",
            timeoutSeconds: 45);
        summary.RecordCount = records.Count;
        ct.ThrowIfCancellationRequested();

        foreach (Dictionary<string, object?> r in records)
        {
            string source = r.Str("SourceName") ?? "";
            int id = r.Int("EventIdentifier") ?? 0;
            DateTime? when = r.Dmtf("TimeGenerated");
            string[] ins = r.TryGetValue("InsertionStrings", out object? v) && v is string[] a ? a : Array.Empty<string>();
            string? product = r.Str("ProductName");
            string? Ins(int i) => i < ins.Length && !string.IsNullOrWhiteSpace(ins[i]) ? ins[i].Trim() : null;

            if (source == "Application Hang" && id == 1002)
            {
                string? exe = Ins(0) ?? product;
                if (exe != null)
                {
                    Count(summary.AppHangs, exe.ToLowerInvariant(), exe, when, Ins(5));
                }
            }
            else if (source == UpdateSource && product != null && id is 19 or 20)
            {
                if (id == 19)
                {
                    if (when is DateTime ok && (!updateSuccess.TryGetValue(product, out DateTime prev) || ok > prev))
                    {
                        updateSuccess[product] = ok;
                    }
                }
                else
                {
                    updateFailures.TryGetValue(product, out var f);
                    bool newer = f.Last == null || when > f.Last;
                    updateFailures[product] = (f.Count + 1, newer ? when : f.Last, newer ? Ins(0) : f.Code);
                }
            }
            else if (source == "MsiInstaller" && id == 1033)
            {
                // "Windows Installer installed the product": name, version, language, status, manufacturer. 0 is success.
                string? code = Ins(3);
                if (code != null && code != "0")
                {
                    string name = Ins(0) ?? product ?? "Unknown product";
                    Count(summary.FailedInstalls, name.ToLowerInvariant(), name, when, code);
                }
            }
        }

        // An update that failed and then installed is history, not a problem.
        foreach (KeyValuePair<string, (int Count, DateTime? Last, string? Code)> pair in updateFailures)
        {
            string title = pair.Key;
            var f = pair.Value;
            if (updateSuccess.TryGetValue(title, out DateTime ok) && (f.Last == null || ok > f.Last))
            {
                continue;
            }

            Match store = StorePackage.Match(title);
            var item = new CountItem
            {
                Key = title.ToLowerInvariant(),
                Label = store.Success ? store.Groups["name"].Value : title,
                Count = f.Count,
                LastSeen = f.Last,
                Extra = f.Code,
            };
            (store.Success ? summary.FailedStoreUpdates : summary.FailedUpdates).Add(item);
        }

        foreach (List<CountItem> list in new[] { summary.AppHangs, summary.FailedUpdates, summary.FailedStoreUpdates, summary.FailedInstalls })
        {
            list.Sort((x, y) => y.Count != x.Count ? y.Count.CompareTo(x.Count) : Nullable.Compare(y.LastSeen, x.LastSeen));
        }

        ct.ThrowIfCancellationRequested();
        ReadStabilityIndex(summary, since);

        ctx.Report.Reliability = summary;
        ctx.WriteRaw("reliability.json", JsonSerializer.Serialize(summary, ReportStore.JsonOptions));

        if (summary.StabilityIndex == null && summary.RecordCount == 0)
        {
            // Reliability Monitor's data collection can be turned off (its scheduled task or by policy).
            status.IsPartial = true;
            status.Detail = "Reliability Monitor has no data on this PC";
            return Task.CompletedTask;
        }

        var parts = new List<string>();
        if (summary.StabilityIndex is double index)
        {
            parts.Add($"stability index {index.ToString("0.0", CultureInfo.InvariantCulture)} of 10");
        }

        int hangs = summary.AppHangs.Sum(h => h.Count);
        if (hangs > 0)
        {
            parts.Add(TextUtil.Plural(hangs, "program hang"));
        }

        int updates = summary.FailedUpdates.Count + summary.FailedStoreUpdates.Count;
        if (updates > 0)
        {
            parts.Add(TextUtil.Plural(updates, "update") + " still failing");
        }

        if (summary.FailedInstalls.Count > 0)
        {
            parts.Add(TextUtil.Plural(summary.FailedInstalls.Count, "failed install"));
        }

        string text = string.Join(", ", parts);
        status.Detail = text.Length == 0 ? "Nothing recorded" : char.ToUpperInvariant(text[0]) + text.Substring(1);
        return Task.CompletedTask;
    }

    private static void ReadStabilityIndex(ReliabilitySummary summary, string since)
    {
        DateTime? latest = null;
        double lowest = double.MaxValue;
        foreach (Dictionary<string, object?> r in Wmi.Query(
                     $"SELECT SystemStabilityIndex, TimeGenerated FROM Win32_ReliabilityStabilityMetrics WHERE TimeGenerated >= '{since}'"))
        {
            if (r.TryGetValue("SystemStabilityIndex", out object? v) && v is double index && r.Dmtf("TimeGenerated") is DateTime when)
            {
                if (latest == null || when > latest)
                {
                    latest = when;
                    summary.StabilityIndex = Math.Round(index, 1);
                }

                if (index < lowest)
                {
                    lowest = index;
                    summary.LowestStabilityIndex = Math.Round(index, 1);
                    summary.LowestAt = when;
                }
            }
        }
    }

    private static void Count(List<CountItem> list, string key, string label, DateTime? when, string? extra)
    {
        CountItem? item = list.FirstOrDefault(i => i.Key == key);
        if (item == null)
        {
            item = new CountItem { Key = key, Label = label, Extra = extra };
            list.Add(item);
        }

        item.Count++;
        if (when != null && (item.LastSeen == null || when > item.LastSeen))
        {
            item.LastSeen = when;
        }
    }
}
