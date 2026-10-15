using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace DiagKit.Report.Collectors;

public sealed class BatteryCollector : ICollector
{
    public string Name => "Battery";
    public string Description => "Battery wear: what it holds now versus when new";
    public int Phase => 1;
    public TimeSpan Timeout => TimeSpan.FromSeconds(45);

    private const string ReportFile = "battery-report.xml";

    public async Task CollectAsync(CollectorContext ctx, CollectorStatus status, CancellationToken ct)
    {
        if (Wmi.Query("SELECT Name FROM Win32_Battery").Count == 0)
        {
            status.Detail = "No battery (desktop)";
            return;
        }

        long design = 0, full = 0;
        int? cycles = null;
        try
        {
            foreach (var r in Wmi.Query("SELECT DesignedCapacity FROM BatteryStaticData", @"root\WMI"))
            {
                design += r.Long("DesignedCapacity") ?? 0;
            }

            foreach (var r in Wmi.Query("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", @"root\WMI"))
            {
                full += r.Long("FullChargedCapacity") ?? 0;
            }

            foreach (var r in Wmi.Query("SELECT CycleCount FROM BatteryCycleCount", @"root\WMI"))
            {
                int? c = r.Int("CycleCount");
                if (c is > 0)
                {
                    cycles = (cycles ?? 0) + c.Value;
                }
            }
        }
        catch (Exception)
        {
            // Some firmware does not expose these classes; the battery report below may still have them.
        }

        ct.ThrowIfCancellationRequested();
        BatteryReportData? report = await ReadBatteryReportAsync(ctx, ct).ConfigureAwait(false);

        var info = new BatteryInfo { DesignCapacity = design, FullChargeCapacity = full, CycleCount = cycles };
        if ((design <= 0 || full <= 0) && report is { DesignCapacity: > 0, FullChargeCapacity: > 0 })
        {
            info.DesignCapacity = report.DesignCapacity;
            info.FullChargeCapacity = report.FullChargeCapacity;
            info.Source = "batteryreport";
        }

        if (info.DesignCapacity <= 0 || info.FullChargeCapacity <= 0)
        {
            status.IsPartial = true;
            status.Detail = "Battery present, but the firmware does not report its capacity";
            return;
        }

        if (report != null)
        {
            info.CycleCount ??= report.CycleCount;
            info.RuntimeMinutes = report.RuntimeMinutes;
            info.DesignRuntimeMinutes = report.DesignRuntimeMinutes;
            info.History = report.History;
        }

        ctx.Report.Battery = info;
        status.Detail = $"Holds {100 - info.WearPercent:0}% of its original capacity" +
                        (info.CycleCount != null ? $", {info.CycleCount} cycles" : "") +
                        (info.RuntimeMinutes is int m ? $", about {TextUtil.Minutes(m)} on a full charge" : "");
    }

    /// <summary>
    /// powercfg's battery report adds what WMI lacks: Windows' runtime estimate, the capacity history since the battery
    /// was fitted, and capacities on firmware that keeps them out of WMI. Written straight into the report's raw folder.
    /// </summary>
    private static async Task<BatteryReportData?> ReadBatteryReportAsync(CollectorContext ctx, CancellationToken ct)
    {
        string path = ctx.RawPath(ReportFile);
        try
        {
            string exe = Path.Combine(Environment.SystemDirectory, "powercfg.exe");
            ProcessResult res = await ProcessRunner.RunAsync(exe, $"/batteryreport /xml /output \"{path}\"", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (res.TimedOut || res.ExitCode != 0 || !File.Exists(path))
            {
                return null;
            }

            return ParseReport(File.ReadAllText(path));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The WMI figures stand on their own; the report only adds to them.
            return null;
        }
    }

    public sealed class BatteryReportData
    {
        public long DesignCapacity { get; set; }
        public long FullChargeCapacity { get; set; }
        public int? CycleCount { get; set; }
        public int? RuntimeMinutes { get; set; }
        public int? DesignRuntimeMinutes { get; set; }
        public List<BatteryCapacityPoint> History { get; set; } = new();
    }

    /// <summary>Reads the XML that <c>powercfg /batteryreport /xml</c> writes. Values may be elements or attributes.</summary>
    public static BatteryReportData ParseReport(string xml)
    {
        XDocument doc = XDocument.Parse(xml);
        XElement root = doc.Root ?? throw new InvalidDataException("empty battery report");
        var data = new BatteryReportData();

        foreach (XElement battery in Children(Child(root, "Batteries"), "Battery"))
        {
            data.DesignCapacity += Long(battery, "DesignCapacity") ?? 0;
            data.FullChargeCapacity += Long(battery, "FullChargeCapacity") ?? 0;
            if (Long(battery, "CycleCount") is long c && c > 0)
            {
                data.CycleCount = (data.CycleCount ?? 0) + (int)Math.Min(c, int.MaxValue);
            }
        }

        XElement? estimates = Child(root, "RuntimeEstimates");
        data.RuntimeMinutes = Minutes(Value(Child(estimates, "FullChargeCapacity"), "ActiveRuntime"));
        data.DesignRuntimeMinutes = Minutes(Value(Child(estimates, "DesignCapacity"), "ActiveRuntime"));

        // Only entries since the battery was last swapped describe this battery.
        foreach (XElement entry in Children(Child(root, "History"), "HistoryEntry"))
        {
            if (Value(entry, "BatteryChanged") == "1")
            {
                data.History.Clear();
            }

            long fullCap = Long(entry, "FullChargeCapacity") ?? 0;
            DateTime? date = Date(Value(entry, "LocalEndDate") ?? Value(entry, "LocalStartDate"));
            if (fullCap > 0 && date != null)
            {
                data.History.Add(new BatteryCapacityPoint
                {
                    Date = date.Value,
                    FullChargeCapacity = fullCap,
                    DesignCapacity = Long(entry, "DesignCapacity") ?? 0,
                });
            }
        }

        data.History.Sort((a, b) => a.Date.CompareTo(b.Date));
        return data;
    }

    private static XElement? Child(XElement? parent, string name)
        => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement? parent, string name)
        => parent?.Elements().Where(e => e.Name.LocalName == name) ?? Enumerable.Empty<XElement>();

    private static string? Value(XElement? e, string name)
    {
        if (e == null)
        {
            return null;
        }

        string? v = e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? Child(e, name)?.Value;
        return string.IsNullOrWhiteSpace(v) ? null : v!.Trim();
    }

    private static long? Long(XElement e, string name)
        => long.TryParse(Value(e, name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long l) ? l : null;

    private static DateTime? Date(string? s)
        => DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime d) ? d : null;

    /// <summary>ISO 8601 durations ("PT2H41M10S"); zero means Windows had no estimate.</summary>
    private static int? Minutes(string? duration)
    {
        if (duration == null)
        {
            return null;
        }

        try
        {
            TimeSpan t = XmlConvert.ToTimeSpan(duration);
            return t.TotalMinutes >= 1 ? (int)Math.Round(t.TotalMinutes) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
