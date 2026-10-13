using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiagKit.Report.Collectors;

public sealed class SensorCollector : ICollector
{
    public string Name => "Temperatures at idle";
    public string Description => "CPU and GPU temperatures, clocks, power and fan speeds";
    public int Phase => 0;
    public TimeSpan Timeout => TimeSpan.FromSeconds(40);

    public async Task CollectAsync(CollectorContext ctx, CollectorStatus status, CancellationToken ct)
    {
        status.Detail = "Loading sensor drivers";
        var monitor = new HardwareMonitor();
        try
        {
            monitor.Open();
        }
        catch (Exception ex)
        {
            monitor.Dispose();
            ctx.Report.Sensors = new SensorSnapshot { Note = "Sensor library failed to start: " + ex.Message };
            status.IsPartial = true;
            status.Detail = "Sensor library failed to start";
            return;
        }

        ctx.Hardware = monitor;

        // Load and clock sensors need two samples to produce a value; temperatures settle quickly.
        monitor.UpdateAll();
        await Task.Delay(800, ct).ConfigureAwait(false);
        monitor.UpdateAll();
        await Task.Delay(800, ct).ConfigureAwait(false);
        monitor.UpdateAll();

        var snap = new SensorSnapshot
        {
            CpuPackageTemp = monitor.CpuPackageTemp(),
            CpuMaxCoreTemp = monitor.CpuMaxCoreTemp(),
            CpuClockMhz = monitor.CpuAverageClockMhz(),
            CpuPowerW = monitor.CpuPackagePower(),
            CpuLoad = monitor.CpuTotalLoad(),
            Gpus = monitor.Gpus(),
        };
        (snap.Fans, snap.BoardTemps) = monitor.Board();
        snap.CpuTemperatureAvailable = snap.CpuPackageTemp != null;

        if (!snap.CpuTemperatureAvailable)
        {
            snap.Note = ctx.Report.System.PawnIoInstalled || Services.SystemProbe.IsPawnIoInstalled()
                ? "PawnIO is installed but no CPU temperature sensor responded. The CPU may be too new for this library version, or the driver was blocked."
                : "CPU temperatures need the PawnIO driver, which is not installed on this PC.";
        }

        ctx.Report.Sensors = snap;
        ctx.WriteRaw("sensors.json", JsonSerializer.Serialize(snap, ReportStore.JsonOptions));

        string cpu = snap.CpuPackageTemp is double t ? $"CPU {t:0} °C" : "CPU temperature unavailable";
        GpuSensors? gpu = snap.Gpus.FirstOrDefault(g => g.Temperature != null);
        string gpuText = gpu?.Temperature is double gt ? $", GPU {gt:0} °C" : "";
        status.Detail = cpu + gpuText;
        if (!snap.CpuTemperatureAvailable)
        {
            status.IsPartial = true;
        }
    }
}
