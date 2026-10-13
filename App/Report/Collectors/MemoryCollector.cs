using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DiagKit.Report.Collectors;

public sealed class MemoryCollector : ICollector
{
    public string Name => "Memory";
    public string Description => "Installed RAM sticks, speed they run at versus their rating";
    public int Phase => 0;
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    // Common kit speeds as they appear in part numbers (F4-3600C16..., CMK16GX4M2B3200C16, BL8G32C16U4B is not matched).
    private static readonly Regex SpeedInPartNumber = new(@"(?<!\d)(2133|2400|2666|2933|3000|3200|3466|3600|3733|3800|4000|4133|4266|4400|4800|5200|5600|6000|6200|6400|6600|6800|7000|7200|7600|8000|8200|8400)(?!\d)");

    private static bool IsPlaceholder(string? maker)
        => string.IsNullOrWhiteSpace(maker) || maker!.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
           maker!.Trim('0', ' ').Length == 0;

    public Task CollectAsync(CollectorContext ctx, CollectorStatus status, CancellationToken ct)
    {
        MemoryInfo mem = ctx.Report.Memory;

        foreach (var r in Wmi.Query("SELECT DeviceLocator, BankLabel, Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
        {
            string? part = r.Str("PartNumber");
            string? locator = r.Str("DeviceLocator");
            string? bank = r.Str("BankLabel");
            string? maker = r.Str("Manufacturer");
            var module = new MemoryModule
            {
                // Some boards name every slot "DIMM 1" and put the channel in BankLabel.
                Slot = locator != null && bank != null && !locator.Contains(bank, StringComparison.OrdinalIgnoreCase)
                    ? $"{bank} / {locator}"
                    : locator ?? bank,
                Manufacturer = IsPlaceholder(maker) ? null : maker,
                PartNumber = part,
                CapacityBytes = r.Long("Capacity") ?? 0,
                RatedSpeed = r.Int("Speed") ?? 0,
                ConfiguredSpeed = r.Int("ConfiguredClockSpeed") ?? 0,
                MemoryType = (r.Int("SMBIOSMemoryType") ?? 0) switch
                {
                    20 => "DDR",
                    21 => "DDR2",
                    24 => "DDR3",
                    26 => "DDR4",
                    29 => "LPDDR3",
                    30 => "LPDDR4",
                    34 => "DDR5",
                    35 => "LPDDR5",
                    _ => null,
                },
            };

            if (part != null)
            {
                Match m = SpeedInPartNumber.Match(part);
                if (m.Success)
                {
                    module.PartNumberSpeedHint = int.Parse(m.Value);
                }
            }

            mem.Modules.Add(module);
        }

        ct.ThrowIfCancellationRequested();

        foreach (var r in Wmi.Query("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
        {
            mem.TotalBytes = (r.Long("TotalVisibleMemorySize") ?? 0) * 1024;
            mem.AvailableBytes = (r.Long("FreePhysicalMemory") ?? 0) * 1024;
        }

        try
        {
            foreach (var r in Wmi.Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
            {
                mem.SlotCount += r.Int("MemoryDevices") ?? 0;
            }
        }
        catch
        {
            // Not reported on some firmware.
        }

        ctx.WriteRaw("memory.json", JsonSerializer.Serialize(mem, ReportStore.JsonOptions));

        long installed = mem.Modules.Sum(m => m.CapacityBytes);
        int speed = mem.Modules.Select(m => m.ConfiguredSpeed).DefaultIfEmpty(0).Max();
        status.Detail = mem.Modules.Count == 0
            ? "No modules reported by firmware"
            : $"{TextUtil.Bytes(installed)} in {TextUtil.Plural(mem.Modules.Count, "stick")}" + (speed > 0 ? $" at {speed} MT/s" : "");
        return Task.CompletedTask;
    }
}
