using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiagKit.Services;
using Microsoft.Win32;

namespace DiagKit.Report.Collectors;

public sealed class SystemCollector : ICollector
{
    public string Name => "System overview";
    public string Description => "Model, board, BIOS, Windows build, CPU and graphics drivers";
    public int Phase => 0;
    public TimeSpan Timeout => TimeSpan.FromSeconds(45);

    public Task CollectAsync(CollectorContext ctx, CollectorStatus status, CancellationToken ct)
    {
        SystemInfo info = ctx.Report.System;
        var errors = new List<string>();

        Try("computer", errors, () =>
        {
            foreach (var r in Wmi.Query("SELECT Manufacturer, Model, PCSystemType, TotalPhysicalMemory FROM Win32_ComputerSystem"))
            {
                info.Manufacturer = r.Str("Manufacturer");
                info.Model = r.Str("Model");
                info.TotalMemoryBytes = r.Long("TotalPhysicalMemory") ?? 0;
                info.ChassisKind = r.Int("PCSystemType") switch
                {
                    1 => "Desktop",
                    2 => "Laptop",
                    3 => "Workstation",
                    4 or 5 or 7 => "Server",
                    8 => "Tablet",
                    _ => "Unknown",
                };
            }
        });
        ct.ThrowIfCancellationRequested();

        Try("chassis", errors, () =>
        {
            // PCSystemType is often 0 on desktops; enclosure type is a better laptop signal.
            if (info.ChassisKind != "Unknown")
            {
                return;
            }

            foreach (var r in Wmi.Query("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
            {
                if (r.TryGetValue("ChassisTypes", out object? v) && v is ushort[] types && types.Length > 0)
                {
                    info.ChassisKind = types[0] switch
                    {
                        8 or 9 or 10 or 14 or 31 or 32 => "Laptop",
                        3 or 4 or 5 or 6 or 7 or 13 or 15 or 16 or 35 or 36 => "Desktop",
                        _ => "Unknown",
                    };
                }
            }
        });

        Try("os", errors, () =>
        {
            foreach (var r in Wmi.Query("SELECT Caption, BuildNumber, LastBootUpTime, InstallDate FROM Win32_OperatingSystem"))
            {
                info.OsName = r.Str("Caption")?.Replace("Microsoft ", "");
                info.OsBuild = int.TryParse(r.Str("BuildNumber"), out int b) ? b : 0;
                info.LastBoot = r.Dmtf("LastBootUpTime");
                info.OsInstallDate = r.Dmtf("InstallDate");
            }

            using RegistryKey? cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            info.OsDisplayVersion = cv?.GetValue("DisplayVersion") as string;
            if (cv?.GetValue("UBR") is int ubr)
            {
                info.OsUbr = ubr;
            }
        });
        ct.ThrowIfCancellationRequested();

        Try("cpu", errors, () =>
        {
            foreach (var r in Wmi.Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
            {
                info.CpuName = r.Str("Name");
                info.CpuCores += r.Int("NumberOfCores") ?? 0;
                info.CpuThreads += r.Int("NumberOfLogicalProcessors") ?? 0;
                info.CpuMaxClockMhz = r.Int("MaxClockSpeed") ?? 0;
            }
        });

        Try("board", errors, () =>
        {
            foreach (var r in Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            {
                string? m = r.Str("Manufacturer");
                string? p = r.Str("Product");
                info.Motherboard = string.Join(" ", new[] { m, p }).Trim();
            }

            foreach (var r in Wmi.Query("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
            {
                info.BiosVersion = r.Str("SMBIOSBIOSVersion");
                info.BiosDate = r.Dmtf("ReleaseDate");
            }
        });
        ct.ThrowIfCancellationRequested();

        Try("gpu", errors, () =>
        {
            foreach (var r in Wmi.Query("SELECT Name, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController"))
            {
                string? name = r.Str("Name");
                string? pnp = r.Str("PNPDeviceID");
                // Skip remote-display and virtual adapters; they clutter the list and have no real driver age.
                if (name == null || (pnp != null && !pnp.StartsWith("PCI", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                info.Gpus.Add(new GpuInfo { Name = name, DriverVersion = r.Str("DriverVersion"), DriverDate = r.Dmtf("DriverDate") });
            }
        });

        Try("state", errors, () =>
        {
            info.PendingReboot = IsRebootPending();
            info.MemoryIntegrity = SystemProbe.IsMemoryIntegrityEnabled();
            info.PawnIoInstalled = SystemProbe.IsPawnIoInstalled();
            using RegistryKey? sb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (sb?.GetValue("UEFISecureBootEnabled") is int enabled)
            {
                info.SecureBoot = enabled == 1;
            }
        });

        ctx.WriteRaw("system.json", JsonSerializer.Serialize(info, ReportStore.JsonOptions));

        status.Detail = info.CpuName != null
            ? $"{info.Manufacturer} {info.Model}".Trim()
            : "Partial information";
        if (errors.Count > 0)
        {
            status.IsPartial = true;
            status.Detail += " (missing: " + string.Join(", ", errors) + ")";
        }

        return Task.CompletedTask;
    }

    private static bool IsRebootPending()
    {
        string[] keys =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
        };
        foreach (string k in keys)
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(k);
            if (key != null)
            {
                return true;
            }
        }

        return false;
    }

    private static void Try(string part, List<string> errors, Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            errors.Add(part);
        }
    }
}
