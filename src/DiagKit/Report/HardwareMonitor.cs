using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace DiagKit.Report;

/// <summary>
/// Wraps LibreHardwareMonitor. CPU temperatures and clocks need the PawnIO driver installed;
/// GPU sensors (NVAPI/ADL/IGCL) work without it.
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;
    private bool _open;

    public HardwareMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsMemoryEnabled = false,
            IsStorageEnabled = false,
            IsControllerEnabled = false,
            IsNetworkEnabled = false,
            IsBatteryEnabled = false,
            IsPsuEnabled = false,
        };
    }

    public void Open()
    {
        _computer.Open();
        _open = true;
    }

    public void UpdateAll()
    {
        foreach (IHardware hw in _computer.Hardware)
        {
            Update(hw);
        }
    }

    public void UpdateCpu()
    {
        foreach (IHardware hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
        {
            Update(hw);
        }
    }

    private static void Update(IHardware hw)
    {
        try
        {
            hw.Update();
        }
        catch
        {
            // A single misbehaving device should not break the snapshot.
        }

        foreach (IHardware sub in hw.SubHardware)
        {
            Update(sub);
        }
    }

    private IEnumerable<IHardware> Cpus => _computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu);

    private static readonly string[] PackageTempNames =
    {
        "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Core (Tdie)", "Package", "Core Max", "CPU Cores",
    };

    public double? CpuPackageTemp()
    {
        List<ISensor> temps = Cpus.SelectMany(c => c.Sensors)
            .Where(s => s.SensorType == SensorType.Temperature && IsValidTemp(s.Value))
            .ToList();
        if (temps.Count == 0)
        {
            return null;
        }

        foreach (string name in PackageTempNames)
        {
            ISensor? hit = temps.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                return hit.Value;
            }
        }

        return temps.Max(s => s.Value);
    }

    public double? CpuMaxCoreTemp()
    {
        var cores = Cpus.SelectMany(c => c.Sensors)
            .Where(s => s.SensorType == SensorType.Temperature && IsValidTemp(s.Value) &&
                        (s.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase) ||
                         s.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase) ||
                         s.Name.StartsWith("CCD", StringComparison.OrdinalIgnoreCase)))
            .Select(s => (double)s.Value!.Value)
            .ToList();
        return cores.Count == 0 ? null : cores.Max();
    }

    /// <summary>Average of per-core clocks. Uses "effective" clocks when available because they include idle states.</summary>
    public double? CpuAverageClockMhz()
    {
        var clocks = Cpus.SelectMany(c => c.Sensors)
            .Where(s => s.SensorType == SensorType.Clock && s.Value is > 100 &&
                        (s.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase) ||
                         s.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (clocks.Count == 0)
        {
            return null;
        }

        var effective = clocks.Where(s => s.Name.Contains("Effective", StringComparison.OrdinalIgnoreCase)).ToList();
        var chosen = effective.Count > 0 ? effective : clocks;
        return chosen.Average(s => (double)s.Value!.Value);
    }

    public double? CpuPackagePower()
    {
        ISensor? s = Cpus.SelectMany(c => c.Sensors)
            .Where(x => x.SensorType == SensorType.Power && x.Value is > 0)
            .OrderByDescending(x => x.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        return s?.Value;
    }

    public double? CpuTotalLoad()
    {
        ISensor? s = Cpus.SelectMany(c => c.Sensors)
            .FirstOrDefault(x => x.SensorType == SensorType.Load &&
                                 x.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase));
        return s?.Value;
    }

    public List<GpuSensors> Gpus()
    {
        var result = new List<GpuSensors>();
        foreach (IHardware gpu in _computer.Hardware.Where(h =>
                     h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel))
        {
            ISensor[] sensors = gpu.Sensors;
            result.Add(new GpuSensors
            {
                Name = gpu.Name,
                Temperature = Pick(sensors, SensorType.Temperature, "GPU Core"),
                HotSpot = Pick(sensors, SensorType.Temperature, "GPU Hot Spot"),
                Load = Pick(sensors, SensorType.Load, "GPU Core"),
                Power = Pick(sensors, SensorType.Power, "GPU Package") ?? Pick(sensors, SensorType.Power, "GPU Power")
                        ?? sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Value is > 0)?.Value,
                FanRpm = sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Value != null)?.Value,
            });
        }

        return result;
    }

    public (List<NamedReading> Fans, List<NamedReading> Temps) Board()
    {
        var fans = new List<NamedReading>();
        var temps = new List<NamedReading>();
        foreach (IHardware board in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Motherboard))
        {
            foreach (IHardware chip in board.SubHardware)
            {
                foreach (ISensor s in chip.Sensors)
                {
                    if (s.Value == null)
                    {
                        continue;
                    }

                    if (s.SensorType == SensorType.Fan)
                    {
                        fans.Add(new NamedReading { Name = s.Name, Value = s.Value.Value, Unit = "RPM" });
                    }
                    else if (s.SensorType == SensorType.Temperature && IsValidTemp(s.Value))
                    {
                        temps.Add(new NamedReading { Name = s.Name, Value = s.Value.Value, Unit = "°C" });
                    }
                }
            }
        }

        return (fans, temps);
    }

    private static double? Pick(IEnumerable<ISensor> sensors, SensorType type, string name)
    {
        ISensor? s = sensors.FirstOrDefault(x => x.SensorType == type &&
                                                 string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) &&
                                                 x.Value != null);
        return s?.Value;
    }

    private static bool IsValidTemp(float? v) => v is > 1 and < 150;

    public void Dispose()
    {
        if (_open)
        {
            try
            {
                _computer.Close();
            }
            catch
            {
                // Ignore driver teardown errors.
            }

            _open = false;
        }
    }
}
