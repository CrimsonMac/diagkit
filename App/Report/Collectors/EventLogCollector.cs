using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DiagKit.Report.Collectors;

public sealed class EventLogCollector : ICollector
{
    public string Name => "Event logs";
    public string Description => $"{Days} days of hardware errors, crashes, power losses and disk errors";
    public int Phase => 1;
    public TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public const int Days = 30;
    private static readonly string Window = $"TimeCreated[timediff(@SystemTime) <= {Days * 24L * 3600 * 1000}]";

    private static readonly Regex HarddiskNumber = new(@"Harddisk(\d+)", RegexOptions.IgnoreCase);

    private static readonly Regex HexCode = new(@"0x[0-9a-fA-F]+");

    public async Task CollectAsync(CollectorContext ctx, CollectorStatus status, CancellationToken ct)
    {
        var summary = new EventSummary { WindowDays = Days };

        // Independent queries run in parallel; each one is bounded in event count.
        var tasks = new List<Task>
        {
            Run(summary, "hardware errors (WHEA)", () => ReadWhea(summary), ct),
            Run(summary, "unexpected shutdowns", () => ReadKernelPower(summary), ct),
            Run(summary, "blue screens", () => ReadBugChecks(summary), ct),
            Run(summary, "disk errors", () => ReadDiskErrors(summary), ct),
            Run(summary, "graphics driver resets", () => ReadGpuResets(summary), ct),
            Run(summary, "application crashes", () => ReadAppCrashes(summary), ct),
            Run(summary, "service crashes", () => ReadServiceCrashes(summary), ct),
        };
        await Task.WhenAll(tasks).ConfigureAwait(false);

        ctx.Report.Events = summary;
        ctx.WriteRaw("events.json", JsonSerializer.Serialize(summary, ReportStore.JsonOptions));

        int whea = summary.Whea.Sum(w => w.Count);
        var parts = new List<string>();
        if (whea > 0)
        {
            parts.Add(TextUtil.Plural(whea, "hardware error"));
        }

        if (summary.BugChecks.Count > 0)
        {
            parts.Add(TextUtil.Plural(summary.BugChecks.Count, "blue screen"));
        }

        if (summary.UnexpectedShutdowns > 0)
        {
            parts.Add(TextUtil.Plural(summary.UnexpectedShutdowns, "unexpected shutdown"));
        }

        int disk = summary.DiskErrors.Sum(d => d.Count);
        if (disk > 0)
        {
            parts.Add(TextUtil.Plural(disk, "disk error"));
        }

        status.Detail = parts.Count == 0 ? "No hardware errors, crashes or power losses logged" : string.Join(", ", parts);
        if (summary.Errors.Count > 0)
        {
            status.IsPartial = true;
            status.Detail += $" ({summary.Errors.Count} log queries failed)";
        }
    }

    private static Task Run(EventSummary summary, string what, Action action, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            lock (summary.Errors)
            {
                summary.Errors.Add($"{what}: {ex.Message}");
            }
        }
    }, ct);

    private static void ForEach(string log, string xpath, int max, Action<EventRecord> handle)
    {
        var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        int n = 0;
        while (n < max)
        {
            using EventRecord? record = reader.ReadEvent();
            if (record == null)
            {
                break;
            }

            n++;
            handle(record);
        }
    }

    private static object? Prop(EventRecord r, int index)
    {
        try
        {
            IList<EventProperty> props = r.Properties;
            return index < props.Count ? props[index].Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string PropText(EventRecord r, int index) => Convert.ToString(Prop(r, index), CultureInfo.InvariantCulture) ?? "";

    private static long PropLong(EventRecord r, int index)
    {
        try
        {
            object? v = Prop(r, index);
            return v == null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static void Count(List<CountItem> list, string key, string label, DateTime? when, string? extra = null)
    {
        lock (list)
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

    private void ReadWhea(EventSummary s)
    {
        ForEach("System", $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and {Window}]]", 2000, r =>
        {
            int id = r.Id;
            bool fatal = r.Level is 1 or 2;
            string label = id switch
            {
                1 => "Fatal hardware error",
                17 => "Corrected PCI Express error",
                18 => "Fatal CPU machine-check error",
                19 => "Corrected CPU machine-check error",
                20 => "Fatal PCI Express error",
                46 => "Fatal memory error",
                47 => "Corrected memory error",
                _ => fatal ? $"Fatal hardware error (event {id})" : $"Corrected hardware error (event {id})",
            };
            Count(s.Whea, $"{(fatal ? "fatal" : "corrected")}-{id}", label, r.TimeCreated, fatal ? "fatal" : "corrected");
        });
    }

    private static void ReadKernelPower(EventSummary s)
    {
        ForEach("System", $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41) and {Window}]]", 500, r =>
        {
            long bugcheck = PropLong(r, 0);
            long powerButton = PropLong(r, 6);
            lock (s)
            {
                s.UnexpectedShutdowns++;
                if (bugcheck != 0)
                {
                    s.UnexpectedWithBugcheck++;
                }
                else if (powerButton != 0)
                {
                    s.PowerButtonHolds++;
                }

                if (s.LastUnexpectedShutdown == null || r.TimeCreated > s.LastUnexpectedShutdown)
                {
                    s.LastUnexpectedShutdown = r.TimeCreated;
                }
            }
        });
    }

    private void ReadBugChecks(EventSummary s)
    {
        ForEach("System", $"*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and (EventID=1001) and {Window}]]", 200, r =>
        {
            string raw = PropText(r, 0);
            Match m = HexCode.Match(raw);
            lock (s.BugChecks)
            {
                s.BugChecks.Add(new BugCheckEvent
                {
                    Time = r.TimeCreated,
                    Code = m.Success ? BugChecks.Normalize(m.Value) : "unknown",
                    Raw = raw.Length > 200 ? raw[..200] : raw,
                });
            }
        });
    }

    private void ReadDiskErrors(EventSummary s)
    {
        string providers = string.Join(" or ", new[] { "disk", "Ntfs", "Microsoft-Windows-Ntfs", "stornvme", "storahci" }
            .Select(p => $"Provider[@Name='{p}']"));
        string xpath = $"*[System[({providers}) and (Level=1 or Level=2 or Level=3) and {Window}]]";
        ForEach("System", xpath, 2000, r =>
        {
            string provider = r.ProviderName ?? "disk";
            int id = r.Id;
            string label = DiskEvents.Classify(provider, id).Label;
            string? diskHint = null;
            for (int i = 0; i < 4; i++)
            {
                Match m = HarddiskNumber.Match(PropText(r, i));
                if (m.Success)
                {
                    diskHint = m.Groups[1].Value;
                    break;
                }
            }

            string key = $"{provider}-{id}-{diskHint}";
            Count(s.DiskErrors, key, label, r.TimeCreated, diskHint);
        });
    }

    private void ReadGpuResets(EventSummary s)
    {
        string xpath = $"*[System[((Provider[@Name='Display'] and EventID=4101) or (Provider[@Name='nvlddmkm'] and (Level=1 or Level=2)) or (Provider[@Name='amdkmdag'] and (Level=1 or Level=2))) and {Window}]]";
        ForEach("System", xpath, 500, r =>
        {
            string provider = r.ProviderName ?? "Display";
            string label = provider.Equals("Display", StringComparison.OrdinalIgnoreCase)
                ? "Display driver stopped responding and recovered"
                : provider.Equals("nvlddmkm", StringComparison.OrdinalIgnoreCase)
                    ? "NVIDIA driver error"
                    : "AMD driver error";
            string? driver = provider.Equals("Display", StringComparison.OrdinalIgnoreCase) ? PropText(r, 0) : provider;
            Count(s.GpuResets, provider + "-" + r.Id, label, r.TimeCreated, string.IsNullOrWhiteSpace(driver) ? null : driver);
        });
    }

    private static void ReadAppCrashes(EventSummary s)
    {
        ForEach("Application", $"*[System[Provider[@Name='Application Error'] and (EventID=1000) and {Window}]]", 1000, r =>
        {
            string app = PropText(r, 0);
            string module = PropText(r, 3);
            if (string.IsNullOrWhiteSpace(app))
            {
                return;
            }

            Count(s.AppCrashes, app.ToLowerInvariant(), app, r.TimeCreated, module);
        });

        lock (s.AppCrashes)
        {
            s.AppCrashes.Sort((a, b) => b.Count.CompareTo(a.Count));
        }
    }

    private static void ReadServiceCrashes(EventSummary s)
    {
        ForEach("System", $"*[System[Provider[@Name='Service Control Manager'] and (EventID=7031 or EventID=7034) and {Window}]]", 500, r =>
        {
            string service = PropText(r, 0);
            if (string.IsNullOrWhiteSpace(service))
            {
                return;
            }

            Count(s.ServiceCrashes, service.ToLowerInvariant(), service, r.TimeCreated);
        });

        lock (s.ServiceCrashes)
        {
            s.ServiceCrashes.Sort((a, b) => b.Count.CompareTo(a.Count));
        }
    }
}
