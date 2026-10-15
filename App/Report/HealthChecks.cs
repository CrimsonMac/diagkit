using System.Collections.Generic;
using System.Linq;

namespace DiagKit.Report;

/// <summary>The parts of a health check that can be run on their own (Quick actions, Custom diagnosis).</summary>
public enum CheckId
{
    System,
    Sensors,
    Memory,
    Volumes,
    Storage,
    Events,
    CrashDumps,
    Devices,
    Battery,
    Startup,
    Stress,
}

public sealed record CheckInfo(CheckId Id, string Name, string Description);

public static class HealthChecks
{
    /// <summary>
    /// The checks a user can tick, in the order they appear. The system overview is not listed: every check includes
    /// it, because a report has to say which PC it is about, and the Windows findings come from it.
    /// The load test is chosen separately because it has a duration.
    /// </summary>
    public static IReadOnlyList<CheckInfo> Selectable { get; } = new List<CheckInfo>
    {
        new(CheckId.Storage, "Drive health", "SMART data: bad sectors, wear, temperature, error counters"),
        new(CheckId.Volumes, "Free space", "Space left on every internal drive"),
        new(CheckId.Sensors, "Temperatures", "CPU and GPU temperatures, clocks, power and fans at idle"),
        new(CheckId.Memory, "Memory", "Installed RAM sticks and the speed they run at"),
        new(CheckId.Events, "Event logs", "Hardware errors, crashes, power losses and disk errors"),
        new(CheckId.CrashDumps, "Crash dumps", "Which driver each blue screen points at"),
        new(CheckId.Devices, "Devices", "Device Manager entries with a yellow warning"),
        new(CheckId.Battery, "Battery", "Battery wear: what it holds now versus when new"),
        new(CheckId.Startup, "Startup entries", "Unsigned or broken programs that start with Windows (20 to 60 s)"),
    };

    /// <summary>
    /// How a hand-picked set of checks is named on its report. Custom diagnosis and the command line use the same words,
    /// so the same selection reads the same wherever it was started.
    /// </summary>
    public static string CustomScope(ICollection<CheckId> only, int stressSeconds)
    {
        IEnumerable<string> names = Selectable.Where(c => only.Contains(c.Id)).Select(c => c.Name);
        if (stressSeconds > 0)
        {
            names = names.Append($"CPU load test ({(stressSeconds >= 60 && stressSeconds % 60 == 0 ? stressSeconds / 60 + " min" : stressSeconds + " s")})");
        }

        return "Custom check: " + string.Join(", ", names);
    }

    public static string Name(CheckId id) => id switch
    {
        CheckId.System => "System overview",
        CheckId.Stress => "CPU load test",
        _ => Selectable.First(c => c.Id == id).Name,
    };
}
