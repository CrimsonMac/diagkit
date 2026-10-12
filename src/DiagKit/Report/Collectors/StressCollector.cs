using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DiagKit.Interop;

namespace DiagKit.Report.Collectors;

/// <summary>
/// Loads every CPU thread with vectorised floating-point work and samples temperature, clock and power
/// once a second. Clock falls back to Windows performance counters when the sensor driver is missing.
/// </summary>
public sealed class StressCollector : ICollector
{
    private readonly int _seconds;

    public StressCollector(int seconds)
    {
        _seconds = Math.Clamp(seconds, 10, 300);
    }

    public string Name => "CPU load test";
    public string Description => $"{_seconds} seconds at full load to catch overheating and throttling";
    public int Phase => 2;
    public TimeSpan Timeout => TimeSpan.FromSeconds(_seconds + 45);

    private static double _sink;

    public async Task CollectAsync(CollectorContext ctx, CollectorStatus status, CancellationToken ct)
    {
        HardwareMonitor? hw = ctx.Hardware;
        var result = new StressResult { DurationSeconds = _seconds, Threads = Environment.ProcessorCount };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var workers = new List<Thread>();
        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            var t = new Thread(() => Burn(stop.Token))
            {
                IsBackground = true,
                // Below normal keeps DiagKit's own window responsive while every core is busy.
                Priority = ThreadPriority.BelowNormal,
                Name = "DiagKit load " + i,
            };
            workers.Add(t);
        }

        using var perf = new PerfClock();
        DateTime start = DateTime.UtcNow;
        try
        {
            foreach (Thread t in workers)
            {
                t.Start();
            }

            while (true)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                double elapsed = (DateTime.UtcNow - start).TotalSeconds;

                double? temp = null, clock = null, power = null;
                if (hw != null)
                {
                    try
                    {
                        hw.UpdateCpu();
                        temp = hw.CpuPackageTemp();
                        clock = hw.CpuAverageClockMhz();
                        power = hw.CpuPackagePower();
                    }
                    catch
                    {
                        // Keep sampling with whatever still works.
                    }
                }

                string source = "sensors";
                if (clock == null)
                {
                    clock = perf.Sample();
                    source = "Windows performance counters";
                }

                result.ClockSource = clock == null ? "" : source;
                result.Samples.Add(new StressSample { Seconds = Math.Round(elapsed, 1), CpuTemp = temp, ClockMhz = clock, PowerW = power });

                var parts = new List<string> { $"{(int)elapsed} of {_seconds} s" };
                if (temp != null)
                {
                    parts.Add($"{temp:0} °C");
                }

                if (clock != null)
                {
                    parts.Add($"{clock / 1000:0.00} GHz");
                }

                if (power != null)
                {
                    parts.Add($"{power:0} W");
                }

                status.Detail = string.Join(", ", parts);
                if (elapsed >= _seconds)
                {
                    break;
                }
            }

            result.Completed = true;
        }
        finally
        {
            stop.Cancel();
            foreach (Thread t in workers)
            {
                t.Join(2000);
            }
        }

        Analyse(result);
        ctx.Report.Stress = result;

        status.Detail = result.MaxTemp is double max
            ? $"Peaked at {max:0} °C" + (result.ClockDropPercent is double drop && drop > 0 ? $", clocks fell {drop:0}% over the run" : ", clocks held steady")
            : result.ClockDropPercent is double d2
                ? (d2 > 0 ? $"No CPU temperature available; clocks fell {d2:0}% over the run" : "No CPU temperature available; clocks held steady")
                : "Load ran, but no CPU sensors were readable";
        if (result.MaxTemp == null)
        {
            status.IsPartial = true;
        }
    }

    internal static void Analyse(StressResult r)
    {
        List<StressSample> s = r.Samples;
        r.MaxTemp = s.Where(x => x.CpuTemp != null).Select(x => x.CpuTemp!.Value).DefaultIfEmpty(double.NaN).Max();
        if (double.IsNaN(r.MaxTemp.Value))
        {
            r.MaxTemp = null;
        }

        double? maxPower = s.Where(x => x.PowerW != null).Select(x => x.PowerW!.Value).DefaultIfEmpty(double.NaN).Max();
        r.MaxPowerW = double.IsNaN(maxPower.Value) ? null : maxPower;

        // Early window skips the first two seconds (ramp-up); late window is the final third of the run.
        var early = s.Where(x => x.ClockMhz != null && x.Seconds >= 2 && x.Seconds <= 8).Select(x => x.ClockMhz!.Value).ToList();
        double lateFrom = r.DurationSeconds * 2.0 / 3.0;
        var late = s.Where(x => x.ClockMhz != null && x.Seconds >= lateFrom).Select(x => x.ClockMhz!.Value).ToList();
        if (early.Count > 0 && late.Count > 0)
        {
            r.EarlyClockMhz = Math.Round(early.Average());
            r.LateClockMhz = Math.Round(late.Average());
            r.ClockDropPercent = r.EarlyClockMhz > 0 ? Math.Round((r.EarlyClockMhz.Value - r.LateClockMhz.Value) * 100.0 / r.EarlyClockMhz.Value, 1) : null;
        }
    }

    private static void Burn(CancellationToken token)
    {
        // Converges to 1.0 so values never become denormal or infinite (which would change the workload).
        var a = new Vector<double>(0.999999);
        var b = new Vector<double>(0.000001);
        Vector<double> x0 = Vector<double>.One, x1 = Vector<double>.One * 0.5, x2 = Vector<double>.One * 0.25, x3 = Vector<double>.One * 0.125;
        while (!token.IsCancellationRequested)
        {
            for (int i = 0; i < 200_000; i++)
            {
                x0 = x0 * a + b;
                x1 = x1 * a + b;
                x2 = x2 * a + b;
                x3 = x3 * a + b;
            }
        }

        _sink = (x0 + x1 + x2 + x3)[0];
    }

    /// <summary>
    /// Effective CPU frequency = "Processor Frequency" (base MHz) x "% Processor Performance" / 100, read through PDH.
    /// Works without any driver; it is the same value Task Manager shows as "Speed". PDH needs two collections
    /// for rate counters, so the first one happens in the constructor, before the load starts.
    /// </summary>
    private sealed class PerfClock : IDisposable
    {
        private IntPtr _query;
        private readonly IntPtr _performance;
        private readonly IntPtr _frequency;
        private readonly bool _ok;

        public PerfClock()
        {
            try
            {
                if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0 ||
                    NativeMethods.PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _performance) != 0 ||
                    NativeMethods.PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out _frequency) != 0)
                {
                    return;
                }

                _ok = NativeMethods.PdhCollectQueryData(_query) == 0;
            }
            catch
            {
                _ok = false;
            }
        }

        public double? Sample()
        {
            if (!_ok)
            {
                return null;
            }

            try
            {
                if (NativeMethods.PdhCollectQueryData(_query) != 0 ||
                    NativeMethods.PdhGetFormattedCounterValue(_performance, NativeMethods.PDH_FMT_DOUBLE, IntPtr.Zero, out var perf) != 0 ||
                    NativeMethods.PdhGetFormattedCounterValue(_frequency, NativeMethods.PDH_FMT_DOUBLE, IntPtr.Zero, out var freq) != 0)
                {
                    return null;
                }

                // CStatus 0 = valid data, 1 = new data; anything else is an error state.
                if (perf.CStatus > 1 || freq.CStatus > 1 || perf.DoubleValue <= 0 || freq.DoubleValue <= 0)
                {
                    return null;
                }

                return freq.DoubleValue * perf.DoubleValue / 100.0;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero)
            {
                NativeMethods.PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }
        }
    }
}
