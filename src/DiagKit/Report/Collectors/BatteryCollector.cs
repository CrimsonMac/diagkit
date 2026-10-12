using System;
using System.Threading;
using System.Threading.Tasks;

namespace DiagKit.Report.Collectors;

public sealed class BatteryCollector : ICollector
{
    public string Name => "Battery";
    public string Description => "Battery wear: what it holds now versus when new";
    public int Phase => 1;
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public Task CollectAsync(CollectorContext ctx, CollectorStatus status, CancellationToken ct)
    {
        if (Wmi.Query("SELECT Name FROM Win32_Battery").Count == 0)
        {
            status.Detail = "No battery (desktop)";
            return Task.CompletedTask;
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
            // Some firmware does not expose these classes; handled below.
        }

        if (design <= 0 || full <= 0)
        {
            status.IsPartial = true;
            status.Detail = "Battery present, but the firmware does not report its capacity";
            return Task.CompletedTask;
        }

        var info = new BatteryInfo { DesignCapacity = design, FullChargeCapacity = full, CycleCount = cycles };
        ctx.Report.Battery = info;
        status.Detail = $"Holds {100 - info.WearPercent:0}% of its original capacity" + (cycles != null ? $", {cycles} cycles" : "");
        return Task.CompletedTask;
    }
}
