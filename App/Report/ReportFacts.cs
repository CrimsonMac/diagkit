using System;
using System.Collections.Generic;
using System.Linq;

namespace DiagKit.Report;

public sealed record Fact(string Label, string Value);

public sealed record Reading(string Label, string Value, string Unit, Severity Severity);

public sealed record DriveRow(string Name, string Kind, string Size, string Health, Severity Severity, string Temperature, string PowerOn, string Wear);

/// <summary>Derived, display-ready facts. Shared by the WPF viewer and the HTML export so they always agree.</summary>
public static class ReportFacts
{
    private static readonly string[] PlaceholderModels =
    {
        "System Product Name", "To Be Filled By O.E.M.", "Default string", "System Version", "Not Applicable", "None",
    };

    public static int Count(DiagnosticReport r, Severity s) => r.Findings.Count(f => f.Severity == s);

    public static int FailedSteps(DiagnosticReport r) => r.Collectors.Count(c => c.Status is "failed" or "timeout");

    public static Severity Worst(DiagnosticReport r)
    {
        Severity worst = r.Findings.Count == 0 ? Severity.Ok : r.Findings.Max(f => f.Severity);
        // A clean result with failed steps is incomplete, not healthy. A partial check that ran cleanly and found
        // nothing (a desktop's battery check) is simply clean.
        bool empty = r.Findings.Count == 0 && r.Scope == null;
        return worst <= Severity.Info && (FailedSteps(r) > 0 || empty) ? Severity.Info : worst;
    }

    public static string Verdict(DiagnosticReport r)
    {
        int crit = Count(r, Severity.Critical);
        int warn = Count(r, Severity.Warning);
        if (crit > 0)
        {
            return crit == 1 ? "1 problem needs attention" : $"{crit} problems need attention";
        }

        if (warn > 0)
        {
            return warn == 1 ? "1 thing is worth fixing" : $"{warn} things are worth fixing";
        }

        int failed = FailedSteps(r);
        if (r.Findings.Count == 0 && (r.Scope == null || failed > 0))
        {
            return "No results";
        }

        if (failed > 0)
        {
            return failed == 1 ? "Nothing found, but 1 step failed" : $"Nothing found, but {failed} steps failed";
        }

        return "Nothing needs attention";
    }

    public static string VerdictDetail(DiagnosticReport r)
    {
        int crit = Count(r, Severity.Critical);
        int warn = Count(r, Severity.Warning);
        if (crit > 0)
        {
            return warn > 0
                ? $"Start with the critical items at the top. There {(warn == 1 ? "is" : "are")} also {TextUtil.Plural(warn, "smaller issue")} below them."
                : "Start with the critical items at the top; they risk data loss or more crashes.";
        }

        if (warn > 0)
        {
            return "Nothing urgent, but these affect stability or performance.";
        }

        if (FailedSteps(r) > 0 || (r.Findings.Count == 0 && r.Scope == null))
        {
            return "Some checks could not run, so this result is incomplete. See how the check ran, at the bottom.";
        }

        if (r.Scope != null)
        {
            return r.Findings.Count == 0
                ? "The parts you checked had nothing to report. Run the full health check for the whole picture."
                : "Nothing to fix in the parts you checked. Run the full health check for the whole picture.";
        }

        return "Drives, crash history, hardware errors and temperatures all look normal.";
    }

    public static string MachineTitle(DiagnosticReport r)
    {
        SystemInfo s = r.System;
        string model = string.Join(" ", new[] { s.Manufacturer, s.Model }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        bool placeholder = string.IsNullOrWhiteSpace(s.Model) || PlaceholderModels.Any(p => s.Model!.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (placeholder)
        {
            model = string.IsNullOrWhiteSpace(s.Motherboard) ? "Custom-built PC" : $"Custom PC on {s.Motherboard}";
        }

        return model;
    }

    public static List<Fact> Overview(DiagnosticReport r)
    {
        SystemInfo s = r.System;
        var facts = new List<Fact>
        {
            new("Computer", $"{r.MachineName}, {MachineTitle(r)}"),
        };

        if (!string.IsNullOrWhiteSpace(s.Motherboard))
        {
            facts.Add(new Fact("Motherboard", s.Motherboard!));
        }

        if (s.BiosVersion != null)
        {
            facts.Add(new Fact("BIOS", $"{s.BiosVersion}, {TextUtil.Date(s.BiosDate)}"));
        }

        if (s.CpuName != null)
        {
            facts.Add(new Fact("Processor", $"{s.CpuName} ({s.CpuCores} cores, {s.CpuThreads} threads)"));
        }

        foreach (GpuInfo g in s.Gpus)
        {
            string driver = (g.DriverVersion, g.DriverDate) switch
            {
                (null, null) => "",
                (null, _) => $", driver from {TextUtil.Date(g.DriverDate)}",
                (_, null) => $", driver {g.DriverVersion}",
                _ => $", driver {g.DriverVersion} from {TextUtil.Date(g.DriverDate)}",
            };
            facts.Add(new Fact("Graphics", g.Name + driver));
        }

        if (r.Memory.Modules.Count > 0)
        {
            long total = r.Memory.Modules.Sum(m => m.CapacityBytes);
            int speed = r.Memory.Modules.Select(m => m.ConfiguredSpeed).DefaultIfEmpty(0).Max();
            string type = r.Memory.Modules.Select(m => m.MemoryType).FirstOrDefault(t => t != null) ?? "";
            facts.Add(new Fact("Memory", $"{TextUtil.Bytes(total)} {type}".Trim() +
                                         $", {TextUtil.Plural(r.Memory.Modules.Count, "stick")}" +
                                         (r.Memory.SlotCount > 0 ? $" in {r.Memory.SlotCount} slots" : "") +
                                         (speed > 0 ? $", {speed} MT/s" : "")));
        }
        else if (s.TotalMemoryBytes > 0)
        {
            facts.Add(new Fact("Memory", TextUtil.Bytes(s.TotalMemoryBytes)));
        }

        if (s.OsName != null)
        {
            facts.Add(new Fact("Windows", $"{s.OsName} {s.OsDisplayVersion}, build {s.OsBuild}.{s.OsUbr}".Replace("  ", " ")));
        }

        if (s.LastBoot is DateTime boot)
        {
            facts.Add(new Fact("Last restart", $"{TextUtil.DateTimeShort(boot)} ({TextUtil.Ago(boot)})"));
        }

        facts.Add(new Fact("Security", string.Join(", ", new[]
        {
            s.SecureBoot switch { true => "Secure Boot on", false => "Secure Boot off", _ => null },
            s.MemoryIntegrity switch { true => "Memory Integrity on", false => "Memory Integrity off", _ => null },
        }.Where(x => x != null))));

        facts.Add(new Fact("Sensor driver", s.PawnIoInstalled ? "PawnIO installed" : "PawnIO not installed (no CPU temperatures)"));
        return facts.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList();
    }

    /// <summary>Hard drives wear faster above about 50 °C; SSDs throttle above about 70 °C.</summary>
    public static bool IsDiskHot(DiskInfo d) => d.Temperature is int t && (d.MediaType == "HDD" ? t > 50 : t > 70);

    public static (string Text, Severity Severity) DiskStatus(DiskInfo d)
    {
        int exit = d.SmartExitStatus ?? 0;
        if (d.SmartPassed == false || (exit & 0x18) != 0)
        {
            return ("Failing", Severity.Critical);
        }

        if (d.PendingSectors > 0 || d.UncorrectableSectors > 0)
        {
            return ("Unreadable sectors", Severity.Critical);
        }

        if (d.CriticalWarning is int cw && (cw & ~0x02) != 0)
        {
            return ("Critical warning", Severity.Critical);
        }

        if (d.ReallocatedSectors >= 100 || d.WindowsHealth == "Unhealthy")
        {
            return ("Unhealthy", Severity.Critical);
        }

        if (d.ReallocatedSectors > 0 || d.MediaErrors > 0)
        {
            return ("Worn", Severity.Warning);
        }

        if (d.PercentageUsed >= 90 || d.LifeLeftPercent <= 10)
        {
            return ("Near end of life", Severity.Warning);
        }

        if (d.WindowsHealth == "Warning")
        {
            return ("Warning", Severity.Warning);
        }

        if (IsDiskHot(d))
        {
            return ("Hot", Severity.Warning);
        }

        if (d.Source == "windows")
        {
            return (d.WindowsHealth ?? "Unknown", d.WindowsHealth == "Healthy" ? Severity.Ok : Severity.Info);
        }

        return ("Healthy", Severity.Ok);
    }

    public static List<DriveRow> Drives(DiagnosticReport r) => r.Disks.Select(d =>
    {
        (string text, Severity sev) = DiskStatus(d);
        string wear = d.PercentageUsed is int pu ? $"{pu}% used"
            : d.LifeLeftPercent is int life ? $"{life}% left"
            : "";
        string name = d.Model + (d.DriveLetters.Count > 0 ? "  " + string.Join(" ", d.DriveLetters) : "");
        return new DriveRow(
            name,
            string.Join(" ", new[] { d.MediaType, d.BusType != null && d.MediaType?.Contains(d.BusType) != true ? d.BusType : null }.Where(x => !string.IsNullOrEmpty(x))),
            TextUtil.DecimalSize(d.SizeBytes),
            text,
            sev,
            d.Temperature is int t ? $"{t} °C" : "",
            d.PowerOnHours is long h ? $"{h:N0} h" : "",
            wear);
    }).ToList();

    public static List<Reading> Readings(DiagnosticReport r)
    {
        var list = new List<Reading>();
        SensorSnapshot? s = r.Sensors;
        if (s?.CpuPackageTemp is double cpu)
        {
            list.Add(new Reading("CPU at idle", $"{cpu:0}", "°C", cpu > 70 ? Severity.Warning : Severity.Ok));
        }

        if (r.Stress?.MaxTemp is double peak)
        {
            list.Add(new Reading("CPU under load", $"{peak:0}", "°C", peak >= 99 ? Severity.Warning : peak >= 90 ? Severity.Info : Severity.Ok));
        }

        if (r.Stress?.LateClockMhz is double clock)
        {
            list.Add(new Reading("Clock under load", $"{clock / 1000:0.00}", "GHz", (r.Stress.ClockDropPercent ?? 0) >= 20 ? Severity.Info : Severity.Ok));
        }
        else if (s?.CpuClockMhz is double idleClock)
        {
            list.Add(new Reading("CPU clock", $"{idleClock / 1000:0.00}", "GHz", Severity.Ok));
        }

        if (r.Stress?.MaxPowerW is double power)
        {
            list.Add(new Reading("CPU power, peak", $"{power:0}", "W", Severity.Ok));
        }
        else if (s?.CpuPowerW is double idlePower)
        {
            list.Add(new Reading("CPU power", $"{idlePower:0}", "W", Severity.Ok));
        }

        foreach (GpuSensors g in s?.Gpus ?? new List<GpuSensors>())
        {
            if (g.Temperature is double gt)
            {
                string label = (s!.Gpus.Count > 1 ? Short(g.Name) : "GPU") + " at idle";
                list.Add(new Reading(label, $"{gt:0}", "°C", gt > 80 ? Severity.Warning : Severity.Ok));
            }
        }

        foreach (NamedReading fan in (s?.Fans ?? new List<NamedReading>()).Where(f => f.Value > 0).Take(4))
        {
            list.Add(new Reading(fan.Name, $"{fan.Value:0}", "RPM", Severity.Ok));
        }

        foreach (DiskInfo d in r.Disks.Where(d => d.Temperature != null).Take(3))
        {
            list.Add(new Reading(Short(d.Model), $"{d.Temperature}", "°C", IsDiskHot(d) ? Severity.Warning : Severity.Ok));
        }

        if (r.Reliability?.StabilityIndex is double stability)
        {
            list.Add(new Reading("Stability index", $"{stability:0.0}", "of 10", Severity.Ok));
        }

        return list;
    }

    private static string Short(string name)
    {
        string[] noise = { "NVIDIA ", "GeForce ", "AMD ", "Radeon ", "Intel(R) ", "Samsung ", "SSD ", "Western Digital ", "WDC " };
        string s = name;
        foreach (string n in noise)
        {
            s = s.Replace(n, "", StringComparison.OrdinalIgnoreCase);
        }

        s = s.Trim();
        return s.Length > 22 ? s[..22].TrimEnd() + "…" : s;
    }
}
