using System;
using System.Text.RegularExpressions;

namespace DiagKit.Report;

/// <summary>
/// One place that knows what each storage event means and how serious it is.
/// Used by the event-log collector (labels) and by the rules (severity), including for older reports.
/// </summary>
public static class DiskEvents
{
    public static (string Label, Severity Severity) Classify(string provider, int id)
    {
        if (provider.Equals("disk", StringComparison.OrdinalIgnoreCase))
        {
            return id switch
            {
                7 => ("Bad block on disk", Severity.Critical),
                52 => ("Drive predicts its own failure", Severity.Critical),
                154 => ("Disk I/O failed with a hardware error", Severity.Critical),
                11 => ("Disk controller error", Severity.Warning),
                15 => ("Disk not ready for access", Severity.Warning),
                51 => ("Error during a paging operation", Severity.Warning),
                153 => ("Disk I/O operation retried", Severity.Warning),
                157 => ("Disk was removed without being ejected", Severity.Info),
                158 => ("Disk with a duplicate ID was attached (a cloned drive or mounted virtual disk)", Severity.Info),
                _ => ($"Disk event {id}", Severity.Warning),
            };
        }

        if (provider.Contains("Ntfs", StringComparison.OrdinalIgnoreCase))
        {
            return id switch
            {
                50 => ("Delayed write failed (data may be lost)", Severity.Critical),
                55 => ("File system structure is corrupt", Severity.Critical),
                98 => ("Volume needs a disk check", Severity.Warning),
                130 or 137 => ("File system repair needed", Severity.Warning),
                140 => ("Failed to flush data to disk", Severity.Warning),
                _ => ($"NTFS event {id}", Severity.Warning),
            };
        }

        if (provider.Equals("storahci", StringComparison.OrdinalIgnoreCase))
        {
            return id == 129 ? ("SATA controller reset the drive", Severity.Warning) : ($"SATA controller event {id}", Severity.Warning);
        }

        if (provider.Equals("stornvme", StringComparison.OrdinalIgnoreCase))
        {
            return id switch
            {
                11 => ("NVMe controller error", Severity.Warning),
                129 => ("NVMe controller reset the drive", Severity.Warning),
                _ => ($"NVMe event {id}", Severity.Warning),
            };
        }

        return ($"{provider} event {id}", Severity.Warning);
    }

    private static readonly Regex KeyPattern = new(@"^(?<provider>.+)-(?<id>\d+)-(?<disk>\d*)$");

    /// <summary>Reads back the "provider-id-disk" key the collector stores, so old reports re-classify correctly.</summary>
    public static (string Label, Severity Severity, string? Disk) FromKey(string key, string fallbackLabel)
    {
        Match m = KeyPattern.Match(key);
        if (!m.Success)
        {
            return (fallbackLabel, Severity.Warning, null);
        }

        (string label, Severity sev) = Classify(m.Groups["provider"].Value, int.Parse(m.Groups["id"].Value));
        string disk = m.Groups["disk"].Value;
        return (label, sev, disk.Length > 0 ? disk : null);
    }
}
