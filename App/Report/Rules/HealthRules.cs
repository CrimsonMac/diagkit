using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiagKit.Report;

/// <summary>
/// Turns collected data into findings. Thresholds are deliberately conservative and wording is hedged
/// where the data cannot prove a cause; every finding says what to do next.
/// </summary>
public static class HealthRules
{
    private static readonly string[] CategoryOrder =
    {
        "Drives", "Crashes", "Hardware errors", "Temperatures", "Memory", "Devices",
        "Storage space", "Windows", "Battery", "Startup", "Programs",
    };

    public static List<Finding> Evaluate(DiagnosticReport r)
    {
        var f = new List<Finding>();
        Drives(r, f);
        DiskEventFindings(r, f);
        Crashes(r, f);
        Shutdowns(r, f);
        Whea(r, f);
        Graphics(r, f);
        Temperatures(r, f);
        LoadTest(r, f);
        Memory(r, f);
        Devices(r, f);
        Space(r, f);
        Windows(r, f);
        Battery(r, f);
        Startup(r, f);
        Programs(r, f);

        return f.OrderByDescending(x => x.Severity)
            .ThenBy(x => Array.IndexOf(CategoryOrder, x.Category) is int i && i >= 0 ? i : 99)
            .ToList();
    }

    private static Finding Add(List<Finding> list, Severity severity, string category, string id, string title, string detail, string? action = null, IEnumerable<string>? evidence = null)
    {
        var finding = new Finding
        {
            Id = id,
            Severity = severity,
            Category = category,
            Title = title,
            Detail = detail,
            Action = action,
            Evidence = evidence?.ToList() ?? new List<string>(),
        };
        list.Add(finding);
        return finding;
    }

    // ---------------------------------------------------------------- Drives

    private static string DiskLabel(DiskInfo d)
    {
        string letters = d.DriveLetters.Count > 0 ? " (" + string.Join(", ", d.DriveLetters) + ")" : "";
        return d.Model + letters;
    }

    private static List<string> DiskEvidence(DiskInfo d)
    {
        var e = new List<string>();
        string kind = string.Join(", ", new[] { d.MediaType, d.BusType, TextUtil.DecimalSize(d.SizeBytes) }.Where(s => !string.IsNullOrEmpty(s)));
        if (kind.Length > 0)
        {
            e.Add(kind);
        }

        if (d.PowerOnHours is long h)
        {
            e.Add($"Powered on for {h:N0} hours (about {h / 24.0 / 365.0:0.#} years of runtime)");
        }

        if (d.Temperature is int t)
        {
            e.Add($"Temperature now: {t} °C");
        }

        if (d.ReallocatedSectors is long re)
        {
            e.Add($"Reallocated sectors: {re}");
        }

        if (d.PendingSectors is long p)
        {
            e.Add($"Pending sectors: {p}");
        }

        if (d.UncorrectableSectors is long u)
        {
            e.Add($"Uncorrectable sectors: {u}");
        }

        if (d.PercentageUsed is int pu)
        {
            e.Add($"Rated write endurance used: {pu}%");
        }

        if (d.LifeLeftPercent is int life)
        {
            e.Add($"Remaining life reported by the drive: {life}%");
        }

        if (d.MediaErrors is long me)
        {
            e.Add($"Media and data integrity errors: {me}");
        }

        if (d.WindowsHealth != null)
        {
            e.Add($"Windows health status: {d.WindowsHealth}");
        }

        if (d.RawFile != null)
        {
            e.Add($"Full SMART data: raw\\{d.RawFile}");
        }

        return e;
    }

    private static void Drives(DiagnosticReport r, List<Finding> f)
    {
        if (r.Disks.Count == 0)
        {
            return;
        }

        bool anyProblem = false;
        foreach (DiskInfo d in r.Disks)
        {
            int firstFinding = f.Count;
            string name = DiskLabel(d);
            List<string> ev = DiskEvidence(d);
            int exit = d.SmartExitStatus ?? 0;
            bool critical = false;

            if (d.SmartPassed == false || (exit & 0x08) != 0)
            {
                Add(f, Severity.Critical, "Drives", "disk-failing", $"{name} reports that it is failing",
                    "The drive's own health self-assessment failed. Drives in this state can stop working at any moment.",
                    "Back up everything on it now, then replace it.", ev);
                critical = true;
            }
            else if (d.PendingSectors > 0 || d.UncorrectableSectors > 0)
            {
                long n = Math.Max(d.PendingSectors ?? 0, d.UncorrectableSectors ?? 0);
                Add(f, Severity.Critical, "Drives", "disk-unreadable", $"{name} has {TextUtil.Plural((int)Math.Min(n, int.MaxValue), "unreadable sector")}",
                    "Parts of the drive could not be read back. Files stored there may already be damaged, and this count usually grows.",
                    "Back up now and plan to replace the drive. A full surface scan (Victoria, or the drive maker's tool) shows how widespread it is.", ev);
                critical = true;
            }
            else if ((exit & 0x10) != 0)
            {
                Add(f, Severity.Critical, "Drives", "disk-threshold", $"{name} has a health value past its failure threshold",
                    "One of the drive's SMART health values dropped below the limit its manufacturer considers safe.",
                    "Back up now and replace the drive.", ev);
                critical = true;
            }

            if (d.CriticalWarning is int cw && cw != 0)
            {
                var reasons = new List<string>();
                if ((cw & 0x01) != 0) reasons.Add("spare capacity is below the safe threshold");
                if ((cw & 0x02) != 0) reasons.Add("temperature is out of range");
                if ((cw & 0x04) != 0) reasons.Add("reliability is degraded by media errors");
                if ((cw & 0x08) != 0) reasons.Add("the drive switched itself to read-only");
                if ((cw & 0x10) != 0) reasons.Add("its power-loss backup failed");
                Add(f, (cw & ~0x02) != 0 ? Severity.Critical : Severity.Warning, "Drives", "nvme-critical",
                    $"{name} raised an NVMe critical warning",
                    "The drive is reporting that " + (reasons.Count > 0 ? string.Join(", and ", reasons) : $"something is wrong (code {cw})") + ".",
                    (cw & ~0x02) != 0 ? "Back up now and replace the drive." : "Improve airflow over the drive or add a heatsink.", ev);
                critical |= (cw & ~0x02) != 0;
            }

            if (d.ReallocatedSectors is long realloc && realloc > 0 && !critical)
            {
                Add(f, realloc >= 100 ? Severity.Critical : Severity.Warning, "Drives", "disk-reallocated",
                    $"{name} has replaced {realloc:N0} bad sectors",
                    "The drive swapped sectors that went bad for spare ones. A small number that stays the same is survivable; a growing number means the drive is wearing out.",
                    "Back up, note this number, and check again in a few weeks. Replace the drive if it rises.", ev);
                critical |= realloc >= 100;
            }

            if (d.MediaErrors is long media && media > 0 && !critical)
            {
                Add(f, Severity.Warning, "Drives", "nvme-media", $"{name} logged {media:N0} data integrity errors",
                    "The SSD hit errors it could not correct when reading its flash memory.",
                    "Back up important data. If the number keeps growing, replace the drive.", ev);
            }

            if (d.PercentageUsed is int used && used >= 70)
            {
                Add(f, used >= 90 ? Severity.Warning : Severity.Info, "Drives", "ssd-endurance",
                    used >= 100 ? $"{name} has used up its rated write endurance" : $"{name} has used {used}% of its rated write endurance",
                    "SSDs are rated for a fixed amount of writing. Many keep working past 100%, but failures become more likely.",
                    used >= 90 ? "Keep good backups and budget for a replacement." : null, ev);
            }
            else if (d.LifeLeftPercent is int life && life <= 20)
            {
                Add(f, life <= 10 ? Severity.Warning : Severity.Info, "Drives", "ssd-life",
                    $"{name} reports {life}% life left",
                    "The drive's own wear indicator is getting low.",
                    "Keep good backups and budget for a replacement.", ev);
            }

            if (ReportFacts.IsDiskHot(d))
            {
                bool hdd = d.MediaType == "HDD";
                Add(f, Severity.Warning, "Drives", "disk-hot", $"{name} is running hot ({d.Temperature} °C)",
                    hdd ? "Hard drives wear faster above about 50 °C." : "SSDs slow themselves down above about 70 °C to protect the flash.",
                    hdd ? "Check that the drive bay has airflow and the case fans work." : "Add a heatsink or improve airflow over the M.2 slot.", ev);
            }

            if (d.CrcErrors is long crc && crc > 0 && d.BusType != "NVMe")
            {
                Add(f, crc > 100 ? Severity.Warning : Severity.Info, "Drives", "disk-crc",
                    $"{name} has {crc:N0} connection errors",
                    "Data got corrupted between the drive and the motherboard and had to be resent. This counter never resets, so it may be from the past.",
                    "If it keeps rising, replace the SATA cable or try another port.", ev);
            }

            if (!critical && d.WindowsHealth == "Unhealthy")
            {
                Add(f, Severity.Critical, "Drives", "disk-windows-unhealthy", $"Windows marks {name} as unhealthy",
                    "Windows' storage service considers this drive unreliable.",
                    "Back up now and replace the drive.", ev);
                critical = true;
            }
            else if (!critical && d.WindowsHealth == "Warning")
            {
                Add(f, Severity.Warning, "Drives", "disk-windows-warning", $"Windows flags a warning for {name}",
                    "Windows' storage service reported a problem with this drive.",
                    "Back up important data and open CrystalDiskInfo from the Tools tab for details.", ev);
            }

            anyProblem |= f.Skip(firstFinding).Any(x => x.Severity >= Severity.Warning);
        }

        bool smartRead = r.Disks.Any(d => d.Source == "smartctl");
        if (!anyProblem)
        {
            Add(f, Severity.Ok, "Drives", "disks-ok",
                $"All {TextUtil.Plural(r.Disks.Count, "drive")} report healthy",
                smartRead ? "No bad sectors, integrity errors or excessive wear in the SMART data." : "Windows reports no problems. SMART details were not read.",
                null, r.Disks.Select(d => DiskLabel(d) + (d.Temperature is int t ? $", {t} °C" : "") + (d.PowerOnHours is long h ? $", {h:N0} h" : "")));
        }

        if (!smartRead)
        {
            Add(f, Severity.Info, "Drives", "smart-missing", "Drive check was limited",
                "Without smartctl, DiagKit only sees Windows' own healthy/unhealthy flag, which misses early signs like reallocated or pending sectors.",
                "Put smartctl.exe in tools\\smartmontools (extract it from the smartmontools installer with 7-Zip) and run the check again.");
        }
    }

    private static void DiskEventFindings(DiagnosticReport r, List<Finding> f)
    {
        if (r.Events == null || r.Events.DiskErrors.Count == 0)
        {
            return;
        }

        var items = r.Events.DiskErrors
            .Select(e => (Item: e, Info: DiskEvents.FromKey(e.Key, e.Label)))
            .OrderByDescending(x => x.Info.Severity).ThenByDescending(x => x.Item.Count)
            .ToList();
        Severity worst = items.Max(x => x.Info.Severity);
        int total = items.Sum(x => x.Item.Count);
        bool anyUsb = false;

        string Where(string? diskNumber)
        {
            if (diskNumber == null || !int.TryParse(diskNumber, out int n))
            {
                return "";
            }

            DiskInfo? disk = r.Disks.FirstOrDefault(d => d.Number == n);
            if (disk == null)
            {
                return $" on disk {n} (not connected now)";
            }

            anyUsb |= disk.BusType == "USB";
            return " on " + DiskLabel(disk) + (disk.BusType == "USB" ? " [USB]" : "");
        }

        List<string> evidence = items
            .Select(x => $"{x.Item.Count}× {x.Info.Label}{Where(x.Info.Disk)}, last {TextUtil.Date(x.Item.LastSeen)}")
            .ToList();

        if (worst == Severity.Info)
        {
            Add(f, Severity.Info, "Drives", "disk-notices",
                $"Windows logged {TextUtil.Plural(total, "disk notice")} in the last {r.Events.WindowDays} days",
                "These are informational events, not errors: for example a drive removed without ejecting, or a cloned drive or mounted virtual disk that shares an ID with another disk.",
                null, evidence);
            return;
        }

        int errors = items.Where(x => x.Info.Severity >= Severity.Warning).Sum(x => x.Item.Count);
        string detail = worst == Severity.Critical
            ? "Some of these mean data could not be read or written, or the file system was damaged."
            : "These are retries, resets and controller errors: the drive or its connection did not respond in time. Usually a cable, port, power or firmware issue rather than a dying drive.";
        if (anyUsb)
        {
            detail += " Some involve USB drives, where unplugging without ejecting causes these too.";
        }

        Add(f, worst, "Drives", "disk-events",
            $"Windows logged {TextUtil.Plural(errors, "disk error")} in the last {r.Events.WindowDays} days",
            detail,
            worst == Severity.Critical
                ? "Back up the affected drive, then run chkdsk /scan on it and check its SMART data above."
                : "Reseat or replace the data cable, update SSD firmware and chipset/storage drivers.",
            evidence);
    }

    // ---------------------------------------------------------------- Crashes

    private static readonly string[] KernelModules = { "ntoskrnl.exe", "ntkrnlmp.exe", "ntkrnlpa.exe", "hal.dll", "ntoskrnl" };

    private static void Crashes(DiagnosticReport r, List<Finding> f)
    {
        DateTime since = r.CreatedAt.AddDays(-(r.Events?.WindowDays ?? 30));
        List<CrashDump> recentDumps = r.CrashDumps.Where(c => c.Time == null || c.Time >= since).ToList();
        int fromEvents = r.Events?.BugChecks.Count ?? 0;
        int count = Math.Max(fromEvents, recentDumps.Count);

        if (count == 0)
        {
            if (r.Events != null)
            {
                string older = r.CrashDumps.Count > 0 ? $" There are {TextUtil.Plural(r.CrashDumps.Count, "older crash dump")} on disk." : "";
                Add(f, Severity.Ok, "Crashes", "no-crashes", $"No blue screens in the last {r.Events.WindowDays} days", "Windows recorded no system crashes." + older);
            }

            return;
        }

        // Dominant driver across all dumps we have, preferring recent ones.
        IEnumerable<CrashDump> pool = recentDumps.Count > 0 ? recentDumps : r.CrashDumps;
        var topDriver = pool.Where(c => !string.IsNullOrEmpty(c.CausedByDriver))
            .GroupBy(c => c.CausedByDriver!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        // Most common bug check code across events and dumps.
        string? topCode = (r.Events?.BugChecks.Select(b => b.Code) ?? Enumerable.Empty<string>())
            .Concat(recentDumps.Where(d => d.BugCheckCode != null).Select(d => d.BugCheckCode!))
            .GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        BugCheckInfo? info = BugChecks.Lookup(topCode);

        bool kernel = topDriver != null && KernelModules.Contains(topDriver.Key, StringComparer.OrdinalIgnoreCase);
        // Hardware-class bug checks (0x124, 0x101, 0x7A...) always name the kernel; that is expected, not a clue.
        bool hardwareCode = info?.Cause is CrashCause.Cpu or CrashCause.Memory or CrashCause.Storage or CrashCause.Graphics;
        var detail = new List<string>();
        if (info != null)
        {
            detail.Add($"Most common: {info.Name}. {info.Meaning}");
        }

        if (topDriver != null)
        {
            CrashDump sample = topDriver.First();
            if (kernel && hardwareCode)
            {
                detail.Add("The dumps name the Windows kernel, which is expected for this kind of crash: the error came from the hardware, not a driver.");
            }
            else if (kernel)
            {
                detail.Add($"{TextUtil.Plural(topDriver.Count(), "crash", "crashes")} point at the Windows kernel itself. That is rarely the real cause: it usually means unstable or faulty RAM, an unstable overclock, or a driver that corrupted memory earlier.");
            }
            else
            {
                string desc = string.Join(", ", new[] { sample.DriverDescription, sample.DriverCompany }.Where(s => !string.IsNullOrWhiteSpace(s)));
                detail.Add($"{TextUtil.Plural(topDriver.Count(), "crash", "crashes")} point at {topDriver.Key}" + (desc.Length > 0 ? $" ({desc})." : "."));
            }
        }
        else if (r.CrashDumps.Count == 0)
        {
            detail.Add("No crash dumps were saved, so the responsible driver is unknown. Dumps may be disabled or were cleaned up.");
        }

        CrashCause cause = info?.Cause ?? CrashCause.Unknown;
        if (kernel && cause is CrashCause.Driver or CrashCause.Unknown)
        {
            cause = CrashCause.Memory;
        }

        if (!kernel && topDriver != null && cause == CrashCause.Unknown)
        {
            cause = CrashCause.Driver;
        }

        string action = cause switch
        {
            CrashCause.Memory => "Turn off XMP/EXPO and any overclock, then test the RAM with TestMem5 (or MemTest86 from a boot stick).",
            CrashCause.Cpu => "Undo CPU overclocks and undervolts, check temperatures under load, and update the BIOS (it carries CPU microcode fixes).",
            CrashCause.Storage => "Check the drive's health above, reseat or replace its cable, and update SSD firmware and storage drivers.",
            CrashCause.Graphics => "Clean-reinstall the graphics driver with DDU, remove GPU overclocks, and check GPU temperature under load.",
            CrashCause.Driver when topDriver != null => $"Update or roll back the driver behind {topDriver.Key}. If it belongs to software you can uninstall (antivirus, VPN, RGB tools), try removing it.",
            CrashCause.Driver => "Update chipset, graphics, network and storage drivers from the manufacturers' sites.",
            _ => "Open BlueScreenView from the Tools tab for the full crash details.",
        };

        var evidence = recentDumps.OrderByDescending(d => d.Time).Take(10)
            .Select(d => $"{TextUtil.DateTimeShort(d.Time)}: {(d.BugCheckCode != null ? BugChecks.Describe(d.BugCheckCode) : d.BugCheckString ?? "unknown code")}" +
                         (d.CausedByDriver != null ? $", {d.CausedByDriver}" : ""))
            .ToList();
        if (evidence.Count == 0 && r.Events != null)
        {
            evidence = r.Events.BugChecks.OrderByDescending(b => b.Time).Take(10)
                .Select(b => $"{TextUtil.DateTimeShort(b.Time)}: {BugChecks.Describe(b.Code)}").ToList();
        }

        Add(f, count >= 3 ? Severity.Critical : Severity.Warning, "Crashes", "bluescreens",
            $"{TextUtil.Plural(count, "blue screen")} in the last {r.Events?.WindowDays ?? 30} days",
            string.Join(" ", detail), action, evidence);
    }

    private static void Shutdowns(DiagnosticReport r, List<Finding> f)
    {
        if (r.Events == null)
        {
            return;
        }

        int n = r.Events.UnexpectedShutdowns - r.Events.UnexpectedWithBugcheck;
        if (n <= 0)
        {
            return;
        }

        bool laptop = r.System.ChassisKind == "Laptop";
        string holds = r.Events.PowerButtonHolds > 0
            ? $" In {TextUtil.Plural(r.Events.PowerButtonHolds, "case")} the power button was held down, which people do when a PC freezes."
            : "";
        Add(f, n >= 2 ? Severity.Warning : Severity.Info, "Crashes", "unexpected-shutdowns",
            $"{TextUtil.Plural(n, "sudden shutdown")} without a blue screen",
            "Windows did not shut down cleanly and recorded no crash, so it either lost power, froze completely, or was switched off by force" +
            (laptop ? ", or the battery ran flat." : ".") + holds +
            " Repeated hard freezes or power-offs usually come from the power supply, overheating or an unstable overclock.",
            "If these were not power cuts, run the CPU load test, check temperatures, and test with a known-good power supply if possible.",
            new[] { $"Last one: {TextUtil.DateTimeShort(r.Events.LastUnexpectedShutdown)}" });
    }

    private static void Whea(DiagnosticReport r, List<Finding> f)
    {
        if (r.Events == null)
        {
            return;
        }

        List<CountItem> items = r.Events.Whea;
        IEnumerable<string> Ev(IEnumerable<CountItem> xs) => xs.Select(x => $"{x.Count}× {x.Label}, last {TextUtil.Date(x.LastSeen)}");

        var fatal = items.Where(i => i.Extra == "fatal").ToList();
        var cpuMem = items.Where(i => i.Extra == "corrected" && (i.Key.EndsWith("-19") || i.Key.EndsWith("-47"))).ToList();
        var pcie = items.Where(i => i.Extra == "corrected" && i.Key.EndsWith("-17")).ToList();
        var other = items.Except(fatal).Except(cpuMem).Except(pcie).ToList();

        if (fatal.Count > 0)
        {
            Add(f, Severity.Critical, "Hardware errors", "whea-fatal", $"{TextUtil.Plural(fatal.Sum(x => x.Count), "fatal hardware error")} logged",
                "The CPU, memory or a PCIe device reported an error it could not correct. These cause instant reboots or WHEA_UNCORRECTABLE_ERROR blue screens.",
                "Remove overclocks, undervolts and XMP/EXPO, update the BIOS, then test again. If errors continue at stock settings, suspect the CPU, RAM or motherboard.",
                Ev(fatal));
        }

        if (cpuMem.Count > 0)
        {
            Add(f, Severity.Warning, "Hardware errors", "whea-corrected", $"{TextUtil.Plural(cpuMem.Sum(x => x.Count), "corrected CPU or memory error")}",
                "The hardware caught and fixed errors on its own. A healthy PC at stock settings logs none; they usually mean an overclock or undervolt is right at the edge of stability, or a part is starting to fail.",
                "Back off any CPU/RAM overclock or undervolt. Run TestMem5 and a CPU stress test to see if errors appear.",
                Ev(cpuMem));
        }

        if (pcie.Count > 0)
        {
            int c = pcie.Sum(x => x.Count);
            Add(f, c >= 50 ? Severity.Warning : Severity.Info, "Hardware errors", "whea-pcie", $"{TextUtil.Plural(c, "corrected PCIe error")}",
                "A PCIe link (graphics card, NVMe drive, or chipset device) had errors that were fixed automatically. On some boards this is harmless noise from power saving; large numbers can mean a poorly seated card or a bad riser cable.",
                c >= 50 ? "Reseat the graphics card and NVMe drives, avoid riser cables, or disable PCIe ASPM in the BIOS to test." : null,
                Ev(pcie));
        }

        if (other.Count > 0)
        {
            Add(f, Severity.Info, "Hardware errors", "whea-other", "Other hardware error reports", "Windows logged hardware error reports that DiagKit does not classify.", null, Ev(other));
        }

        if (items.Count == 0)
        {
            Add(f, Severity.Ok, "Hardware errors", "whea-none", "No hardware errors logged",
                $"No CPU, memory or PCIe errors were reported in the last {r.Events.WindowDays} days.");
        }
    }

    private static void Graphics(DiagnosticReport r, List<Finding> f)
    {
        if (r.Events == null || r.Events.GpuResets.Count == 0)
        {
            return;
        }

        int n = r.Events.GpuResets.Sum(x => x.Count);
        Add(f, n >= 3 ? Severity.Warning : Severity.Info, "Crashes", "gpu-resets", $"{TextUtil.Plural(n, "graphics driver error")}",
            "The display driver stopped responding and Windows restarted it, which looks like a brief black screen or a game crashing to the desktop. Causes include driver bugs, GPU overclocks, overheating, or a power supply struggling under load.",
            "Clean-reinstall the graphics driver with DDU, remove GPU overclocks, and check GPU temperatures under load.",
            r.Events.GpuResets.Select(x => $"{x.Count}× {x.Label}" + (x.Extra != null ? $" ({x.Extra})" : "") + $", last {TextUtil.Date(x.LastSeen)}"));
    }

    // ---------------------------------------------------------------- Temperatures

    private static void Temperatures(DiagnosticReport r, List<Finding> f)
    {
        SensorSnapshot? s = r.Sensors;
        if (s == null)
        {
            return;
        }

        if (!s.CpuTemperatureAvailable)
        {
            Add(f, Severity.Info, "Temperatures", "cpu-temp-missing", "CPU temperature could not be read",
                s.Note ?? "No CPU temperature sensor responded.",
                r.System.PawnIoInstalled
                    ? "Open HWiNFO from the Tools tab to check temperatures."
                    : "Run the check again with 'Install the CPU temperature driver' ticked (it installs the signed PawnIO driver), or use HWiNFO.");
        }
        else if (s.CpuPackageTemp is double t)
        {
            string load = s.CpuLoad is double l && l > 25 ? $" CPU load was {l:0}% at the time." : "";
            if (t > 70)
            {
                Add(f, Severity.Warning, "Temperatures", "cpu-idle-hot", $"CPU is hot while mostly idle ({t:0} °C)",
                    "A resting CPU normally sits around 30 to 55 °C. High idle temperatures point to a poorly mounted cooler, dried-out thermal paste, a failed fan or pump, or something running in the background." + load,
                    "Check for background load in Process Explorer first. If it really is idle, reseat the cooler with fresh paste and check that the fan or pump spins.",
                    new[] { $"CPU package: {t:0} °C" });
            }
            else
            {
                Add(f, Severity.Ok, "Temperatures", "cpu-idle-ok", $"CPU temperature is normal at idle ({t:0} °C)", "Cooling looks fine when the PC is not under load." + load);
            }
        }

        foreach (GpuSensors g in s.Gpus)
        {
            if (g.Temperature is double gt && gt > 80)
            {
                Add(f, Severity.Warning, "Temperatures", "gpu-idle-hot", $"{g.Name} is hot while idle ({gt:0} °C)",
                    "Graphics cards usually idle between 30 and 60 °C. Dust, a stopped fan or dried thermal paste are the usual causes.",
                    "Clean the card's heatsink and check that its fans spin up under load.");
            }
        }
    }

    private static void LoadTest(DiagnosticReport r, List<Finding> f)
    {
        StressResult? s = r.Stress;
        if (s == null || !s.Completed)
        {
            return;
        }

        var ev = new List<string> { $"{s.DurationSeconds} s on {s.Threads} threads" };
        if (s.MaxTemp is double mt) ev.Add($"Peak CPU temperature: {mt:0} °C");
        if (s.EarlyClockMhz is double e && s.LateClockMhz is double l) ev.Add($"Average clock: {e / 1000:0.00} GHz at the start, {l / 1000:0.00} GHz at the end ({s.ClockSource})");
        if (s.MaxPowerW is double p) ev.Add($"Peak CPU power: {p:0} W");

        double drop = s.ClockDropPercent ?? 0;
        if (s.MaxTemp is double max)
        {
            if (max >= 99)
            {
                Add(f, Severity.Warning, "Temperatures", "load-throttle", $"CPU reached {max:0} °C under load and is throttling",
                    "The CPU hit its maximum safe temperature and had to slow itself down" + (drop > 5 ? $" (clocks fell {drop:0}%)." : ".") +
                    " It will be slower than it should be in any sustained work.",
                    "Clean dust out of the cooler, reseat it with fresh paste, and check fan curves in the BIOS. On laptops, a cooling pad and a cleaning help.", ev);
            }
            else if (max >= 95 && drop >= 10)
            {
                Add(f, Severity.Warning, "Temperatures", "load-throttle", $"CPU slows down under load ({max:0} °C, clocks −{drop:0}%)",
                    "Temperatures sat near the CPU's limit and clock speeds dropped as the test went on, which is thermal throttling.",
                    "Improve cooling: clean the heatsink, reseat it with fresh paste, and check that fans ramp up.", ev);
            }
            else if (max >= 90)
            {
                Add(f, Severity.Info, "Temperatures", "load-hot", $"CPU runs very hot under full load ({max:0} °C)",
                    "Recent Ryzen and Intel desktop chips boost until they reach roughly 90 to 95 °C by design, so this can be normal. On older CPUs or laptops it suggests the cooling is struggling.",
                    null, ev);
            }
            else
            {
                Add(f, Severity.Ok, "Temperatures", "load-ok", $"CPU stayed at {max:0} °C under full load",
                    drop >= 20 ? $"No overheating. Clocks fell {drop:0}% though, which points at power limits rather than heat." : "No overheating and clock speeds held up.",
                    null, ev);
            }
        }
        else if (s.EarlyClockMhz == null || s.LateClockMhz == null)
        {
            // Never turn "no data" into "no problem".
            Add(f, Severity.Info, "Temperatures", "load-no-data", "The CPU load test could not measure anything",
                "The load ran, but neither CPU temperature nor clock speed could be read, so it cannot say whether the CPU overheats or throttles.",
                "Run the check again with 'Install the CPU temperature driver' ticked, or watch HWiNFO during a stress run.", ev);
        }
        else if (drop >= 20)
        {
            Add(f, Severity.Info, "Temperatures", "load-clock-drop", $"Clock speed fell {drop:0}% during the load test",
                "Without a temperature reading this cannot be pinned on heat. Laptops and many stock Intel desktops lower their clocks after a short boost window by design (power limits).",
                "Repeat the test with 'Install the CPU temperature driver' ticked to see temperatures, or watch HWiNFO during a longer stress run.", ev);
        }
        else
        {
            Add(f, Severity.Ok, "Temperatures", "load-steady", "Clock speeds held steady under full load",
                "No temperature reading was available, but the CPU did not slow down during the test.", null, ev);
        }
    }

    // ---------------------------------------------------------------- Memory

    private static void Memory(DiagnosticReport r, List<Finding> f)
    {
        MemoryInfo m = r.Memory;
        if (m.Modules.Count == 0)
        {
            return;
        }

        int before = f.Count;
        List<string> ev = m.Modules.Select(x =>
            $"{x.Slot ?? "Slot ?"}: " + string.Join(" ", new[] { TextUtil.Bytes(x.CapacityBytes), x.MemoryType, x.Manufacturer, x.PartNumber }.Where(p => !string.IsNullOrWhiteSpace(p))) +
            $", rated {(x.RatedSpeed > 0 ? x.RatedSpeed + " MT/s" : "unknown")}, running {(x.ConfiguredSpeed > 0 ? x.ConfiguredSpeed + " MT/s" : "unknown")}").ToList();

        int configured = m.Modules.Where(x => x.ConfiguredSpeed > 0).Select(x => x.ConfiguredSpeed).DefaultIfEmpty(0).Min();
        int rated = m.Modules.Where(x => x.RatedSpeed > 0).Select(x => x.RatedSpeed).DefaultIfEmpty(0).Min();
        int hint = m.Modules.Where(x => x.PartNumberSpeedHint is > 0).Select(x => x.PartNumberSpeedHint!.Value).DefaultIfEmpty(0).Min();
        bool desktop = r.System.ChassisKind is "Desktop" or "Workstation";
        string xmp = "Enable XMP (Intel) or EXPO/DOCP (AMD) in the BIOS. It is a supported setting and free performance. If the PC was unstable with it on, leave it off.";

        if (configured > 0 && rated > configured * 1.05)
        {
            Add(f, Severity.Info, "Memory", "ram-below-rated", $"RAM runs at {configured} MT/s but is rated for {rated}",
                "The memory is running below the speed the sticks report they support.", xmp, ev);
        }
        else if (configured > 0 && hint > configured * 1.05 && desktop)
        {
            Add(f, Severity.Info, "Memory", "ram-below-kit", $"RAM may be running below its kit speed ({configured} vs {hint} MT/s)",
                $"The part number suggests a {hint} MT/s kit, but it runs at {configured} MT/s. Kit speeds need XMP/EXPO enabled.", xmp, ev);
        }

        int sticks = m.Modules.Count;
        if (sticks == 1 && (m.SlotCount == 0 || m.SlotCount >= 2) && m.Modules[0].MemoryType?.StartsWith("LP") != true)
        {
            Add(f, Severity.Info, "Memory", "ram-single-channel", "Only one RAM stick installed (single-channel)",
                "With one stick the memory runs single-channel, roughly half the bandwidth. It is noticeable in games and especially with integrated graphics.",
                "Add a matching second stick of the same size and speed.", ev);
        }

        int kits = m.Modules.Select(x => (x.PartNumber ?? "").Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (kits > 1)
        {
            Add(f, Severity.Info, "Memory", "ram-mixed", "RAM sticks come from different kits",
                "Mixed sticks often work, but they are a common cause of instability at XMP/EXPO speeds.",
                "If you see crashes, test with only one matched kit installed.", ev);
        }

        long total = m.Modules.Sum(x => x.CapacityBytes);
        if (total > 0 && total < 8L * 1024 * 1024 * 1024)
        {
            Add(f, Severity.Info, "Memory", "ram-small", $"Only {TextUtil.Bytes(total)} of RAM",
                "Windows 11 with a browser open already uses most of this, so the PC will page to disk often.",
                "Upgrade to at least 16 GB if the board allows.", ev);
        }

        if (f.Count == before)
        {
            Add(f, Severity.Ok, "Memory", "ram-ok", "Memory configuration looks right",
                $"{TextUtil.Bytes(total)} in {TextUtil.Plural(sticks, "stick")}" + (configured > 0 ? $", running at {configured} MT/s." : "."), null, ev);
        }
    }

    // ---------------------------------------------------------------- Devices, space, Windows

    private static void Devices(DiagnosticReport r, List<Finding> f)
    {
        if (!r.DevicesChecked)
        {
            return;
        }

        if (r.ProblemDevices.Count == 0)
        {
            Add(f, Severity.Ok, "Devices", "devices-ok", "All devices are working", "Device Manager shows no warnings.");
            return;
        }

        bool blocked = r.ProblemDevices.Any(d => d.ErrorCode is 48 or 52);
        bool missing = r.ProblemDevices.Any(d => d.ErrorCode == 28);
        string detail = "These show a yellow warning in Device Manager.";
        if (blocked && r.System.MemoryIntegrity == true)
        {
            detail += " Memory Integrity is on, and it blocks older drivers with known problems.";
        }

        string action = missing
            ? "Install the missing drivers from the PC or motherboard maker's site (Snappy Driver Installer Origin works offline)."
            : "Update or reinstall each device's driver from the manufacturer's site.";
        Add(f, Severity.Warning, "Devices", "devices-problem", $"{TextUtil.Plural(r.ProblemDevices.Count, "device")} not working properly",
            detail, action, r.ProblemDevices.Select(d => $"{d.Name}: {d.ErrorText} (code {d.ErrorCode})"));
    }

    private static void Space(DiagnosticReport r, List<Finding> f)
    {
        foreach (VolumeInfo v in r.Volumes)
        {
            double pct = v.FreePercent;
            string free = $"{TextUtil.Bytes(v.FreeBytes)} free of {TextUtil.Bytes(v.TotalBytes)} ({pct:0}%)";
            if (v.IsSystem && (v.FreeBytes < 2L * 1024 * 1024 * 1024))
            {
                Add(f, Severity.Critical, "Storage space", "space-system-full", $"{v.Letter} is full ({TextUtil.Bytes(v.FreeBytes)} left)",
                    "Windows needs free space for updates, the page file and temporary files. With this little left, updates fail and programs can crash.",
                    "Open WizTree from the Tools tab to find what is using the space.", new[] { free });
            }
            else if (v.IsSystem && (pct < 10 || v.FreeBytes < 15L * 1024 * 1024 * 1024))
            {
                Add(f, Severity.Warning, "Storage space", "space-system-low", $"{v.Letter} is nearly full",
                    "Below about 10% free, Windows updates start failing and SSDs slow down.",
                    "Open WizTree from the Tools tab to find what is using the space.", new[] { free });
            }
            else if (!v.IsSystem && pct < 5)
            {
                Add(f, Severity.Info, "Storage space", "space-data-low", $"{v.Letter} is almost full", "A data drive is nearly out of space.", null, new[] { free });
            }
        }
    }

    private sealed record Lifecycle(int Build, string Name, DateTime HomePro, DateTime Enterprise);

    private static readonly Lifecycle[] Win11 =
    {
        new(22000, "21H2", new DateTime(2023, 10, 10), new DateTime(2024, 10, 8)),
        new(22621, "22H2", new DateTime(2024, 10, 8), new DateTime(2025, 10, 14)),
        new(22631, "23H2", new DateTime(2025, 11, 11), new DateTime(2026, 11, 10)),
        new(26100, "24H2", new DateTime(2026, 10, 13), new DateTime(2027, 10, 12)),
        new(26200, "25H2", new DateTime(2027, 10, 12), new DateTime(2028, 10, 10)),
    };

    private static void Windows(DiagnosticReport r, List<Finding> f)
    {
        SystemInfo s = r.System;
        DateTime now = r.CreatedAt;
        string version = s.OsDisplayVersion ?? s.OsBuild.ToString();
        bool ltsc = s.OsName?.Contains("LTSC", StringComparison.OrdinalIgnoreCase) == true;
        bool enterprise = s.OsName?.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) == true ||
                          s.OsName?.Contains("Education", StringComparison.OrdinalIgnoreCase) == true;

        if (s.OsBuild > 0 && s.OsBuild < 22000 && !ltsc)
        {
            Add(f, Severity.Warning, "Windows", "win10-eol", "Windows 10 no longer gets security updates",
                "Free security updates for Windows 10 ended on 14 October 2025. Unless this PC is enrolled in Extended Security Updates, new vulnerabilities stay unpatched.",
                "Upgrade to Windows 11 if the hardware supports it, or enrol in Extended Security Updates.",
                new[] { $"{s.OsName} {version} (build {s.OsBuild}.{s.OsUbr})" });
        }
        else if (Win11.FirstOrDefault(x => x.Build == s.OsBuild) is { } life && !ltsc)
        {
            DateTime end = enterprise ? life.Enterprise : life.HomePro;
            string evidence = $"{s.OsName} {life.Name} (build {s.OsBuild}.{s.OsUbr}), support ends {TextUtil.Date(end)}";
            if (now >= end)
            {
                Add(f, Severity.Warning, "Windows", "win11-eol", $"Windows 11 {life.Name} no longer gets security updates",
                    $"Support for this version ended on {TextUtil.Date(end)}.",
                    "Install the latest feature update through Windows Update.", new[] { evidence });
            }
            else if ((end - now).TotalDays <= 60)
            {
                Add(f, Severity.Info, "Windows", "win11-eol-soon", $"Windows 11 {life.Name} support ends {TextUtil.Date(end)}",
                    "After that date this version stops receiving security updates.",
                    "Install the latest feature update through Windows Update.", new[] { evidence });
            }
        }

        if (s.LastBoot is DateTime boot)
        {
            double days = (now - boot).TotalDays;
            if (days > 14)
            {
                Add(f, Severity.Info, "Windows", "uptime", $"Not restarted in {(int)days} days",
                    "With Fast Startup, shutting down does not reset Windows; only Restart does. Pending updates and driver fixes wait for a restart.",
                    "Restart (not shut down) before testing further.");
            }
        }

        if (s.PendingReboot == true)
        {
            Add(f, Severity.Info, "Windows", "reboot-pending", "A restart is waiting to finish installing updates", "Some updates are installed but not active yet.", "Restart the PC.");
        }

        if (s.BiosDate is DateTime bios && (now - bios).TotalDays > 4 * 365)
        {
            Add(f, Severity.Info, "Windows", "bios-old", $"BIOS is from {bios.Year}",
                "BIOS updates fix memory compatibility, stability and security problems, and carry CPU microcode fixes.",
                "Check the motherboard or PC maker's support page for a newer version and follow their update instructions exactly.",
                new[] { $"{s.Motherboard}, BIOS {s.BiosVersion} from {TextUtil.Date(bios)}" });
        }

        foreach (GpuInfo g in s.Gpus)
        {
            if (g.DriverDate is DateTime dd && (now - dd).TotalDays > 365)
            {
                int months = (int)((now - dd).TotalDays / 30.4);
                Add(f, Severity.Info, "Windows", "gpu-driver-old", $"Graphics driver is {months} months old",
                    $"{g.Name} uses a driver from {TextUtil.Date(dd)}. Newer drivers fix crashes and game issues.",
                    "Install the latest driver from NVIDIA, AMD or Intel (or the laptop maker for hybrid graphics).",
                    new[] { $"{g.Name}, driver {g.DriverVersion}" });
            }
        }
    }

    private static void Battery(DiagnosticReport r, List<Finding> f)
    {
        BatteryInfo? b = r.Battery;
        if (b == null || b.DesignCapacity <= 0)
        {
            return;
        }

        double wear = b.WearPercent;
        var ev = new List<string> { $"Holds {b.FullChargeCapacity:N0} mWh of its original {b.DesignCapacity:N0} mWh" };
        if (b.CycleCount != null)
        {
            ev.Add($"{b.CycleCount} charge cycles");
        }

        if (wear >= 40)
        {
            Add(f, Severity.Warning, "Battery", "battery-worn", $"Battery has lost {wear:0}% of its capacity",
                "Runtime is well below what it was when new, and heavily worn batteries can cause sudden shutdowns under load.",
                "Replace the battery. If it is swelling (lifted touchpad or bottom panel), stop using it immediately.", ev);
        }
        else if (wear >= 20)
        {
            Add(f, Severity.Info, "Battery", "battery-wear", $"Battery has lost {wear:0}% of its capacity", "Normal wear for an older battery.", null, ev);
        }
        else
        {
            Add(f, Severity.Ok, "Battery", "battery-ok", $"Battery holds {100 - wear:0}% of its original capacity", "Little wear so far.", null, ev);
        }
    }

    private static void Startup(DiagnosticReport r, List<Finding> f)
    {
        AutorunSummary? a = r.Autoruns;
        if (a == null)
        {
            return;
        }

        static string Line(AutorunEntry e) => $"{e.Entry} [{e.Category}]" + (e.ImagePath != null ? ": " + e.ImagePath : "");

        if (a.UnusualLocations.Count > 0)
        {
            Add(f, Severity.Warning, "Startup", "autorun-unusual", $"{TextUtil.Plural(a.UnusualLocations.Count, "startup entry", "startup entries")} in unusual places",
                "These hook into Windows in ways normal software rarely needs (image hijacks, Winlogon, WMI, boot execute), and malware favours them. Some security and accessibility tools do use them legitimately.",
                "Look up each entry. Open Autoruns from the Tools tab to inspect, and uncheck to disable.",
                a.UnusualLocations.Take(15).Select(Line));
        }

        if (a.Unsigned.Count > 0)
        {
            Add(f, Severity.Info, "Startup", "autorun-unsigned", a.Unsigned.Count == 1 ? "1 unsigned program starts with Windows" : $"{a.Unsigned.Count} unsigned programs start with Windows",
                "Unsigned does not mean malicious; plenty of small tools are unsigned. But almost all malware is, so these are the ones to recognise.",
                "Check that you recognise each one. Autoruns can submit them to VirusTotal.",
                a.Unsigned.Take(20).Select(Line));
        }

        if (a.Missing.Count > 0)
        {
            Add(f, Severity.Info, "Startup", "autorun-missing", a.Missing.Count == 1 ? "1 startup entry points at a file that no longer exists" : $"{a.Missing.Count} startup entries point at files that no longer exist",
                "Leftovers from software that was uninstalled or moved. Harmless, but they clutter startup.",
                "Delete them in Autoruns.", a.Missing.Take(15).Select(Line));
        }

        if (a.LogonEntries > 15)
        {
            Add(f, Severity.Info, "Startup", "autorun-many", $"{a.LogonEntries} programs launch at sign-in",
                "Each one adds to sign-in time and uses memory in the background.",
                "Disable what you do not need in Task Manager > Startup apps, or in Autoruns.");
        }
    }

    // The kit's exe may have been renamed; its default name is kept so older copies are filtered too.
    private static readonly string[] OwnExeNames = new[] { Path.GetFileName(Environment.ProcessPath ?? ""), "DiagKit.exe" }
        .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static void Programs(DiagnosticReport r, List<Finding> f)
    {
        if (r.Events == null)
        {
            return;
        }

        // DiagKit's own crashes say nothing about the PC being checked.
        var frequent = r.Events.AppCrashes
            .Where(a => a.Count >= 3 && !OwnExeNames.Contains(a.Label, StringComparer.OrdinalIgnoreCase))
            .Take(8).ToList();
        if (frequent.Count > 0)
        {
            bool shell = frequent.Any(a => a.Label.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) ||
                                           a.Label.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase));
            Add(f, shell ? Severity.Warning : Severity.Info, "Programs", "app-crashes", "Programs that crash repeatedly",
                shell
                    ? "Windows' own shell (Explorer or the desktop compositor) keeps crashing. Shell extensions, graphics drivers or system file corruption are the usual causes."
                    : "These programs crashed several times recently. The faulting module shows whether the program itself or something injected into it is to blame.",
                shell ? "Run 'sfc /scannow' and 'DISM /Online /Cleanup-Image /RestoreHealth', and check shell extensions with Autoruns." : null,
                frequent.Select(a => $"{a.Label}: {TextUtil.Plural(a.Count, "crash", "crashes")}" + (string.IsNullOrEmpty(a.Extra) ? "" : $", in {a.Extra}") + $", last {TextUtil.Date(a.LastSeen)}"));
        }

        var services = r.Events.ServiceCrashes.Where(s => s.Count >= 2).Take(8).ToList();
        if (services.Count > 0)
        {
            Add(f, Severity.Info, "Programs", "service-crashes", "Background services that stopped unexpectedly",
                "Windows services that crashed more than once. Often from third-party software.",
                null, services.Select(s => $"{s.Label}: {s.Count} times, last {TextUtil.Date(s.LastSeen)}"));
        }
    }
}
