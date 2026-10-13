using System;
using System.Threading;
using System.Threading.Tasks;

namespace DiagKit.Report.Collectors;

public sealed class DeviceCollector : ICollector
{
    public string Name => "Devices";
    public string Description => "Device Manager entries with a yellow warning";
    public int Phase => 1;
    public TimeSpan Timeout => TimeSpan.FromSeconds(40);

    public Task CollectAsync(CollectorContext ctx, CollectorStatus status, CancellationToken ct)
    {
        foreach (var r in Wmi.Query("SELECT Name, DeviceID, PNPClass, ConfigManagerErrorCode, Present FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
        {
            int code = r.Int("ConfigManagerErrorCode") ?? 0;
            // 22 = disabled on purpose, 45 = not connected right now: neither is a fault.
            if (code is 0 or 22 or 45 || r.Bool("Present") == false)
            {
                continue;
            }

            ctx.Report.ProblemDevices.Add(new ProblemDevice
            {
                Name = r.Str("Name") ?? r.Str("DeviceID") ?? "Unknown device",
                DeviceId = r.Str("DeviceID"),
                Class = r.Str("PNPClass"),
                ErrorCode = code,
                ErrorText = Describe(code),
            });
        }

        ctx.Report.DevicesChecked = true;
        status.Detail = ctx.Report.ProblemDevices.Count == 0
            ? "No devices with problems"
            : TextUtil.Plural(ctx.Report.ProblemDevices.Count, "device") + " with problems";
        return Task.CompletedTask;
    }

    public static string Describe(int code) => code switch
    {
        1 => "Not configured correctly",
        3 => "Driver may be corrupted, or the system is low on memory",
        10 => "Device cannot start",
        12 => "Not enough free resources",
        14 => "Needs a restart",
        18 => "Drivers need reinstalling",
        19 => "Registry configuration is damaged",
        24 => "Device not present or not working",
        28 => "No driver installed",
        29 => "Disabled by firmware",
        31 => "Driver failed to load",
        32 => "Driver service is disabled",
        33 => "Cannot determine required resources",
        37 => "Driver failed to initialise",
        38 => "Previous driver instance still loaded",
        39 => "Driver is missing or corrupted",
        40 => "Driver registry entry is invalid",
        41 => "Driver loaded but cannot find the hardware",
        43 => "Device reported a failure (often USB)",
        48 => "Driver blocked because it has known problems (Memory Integrity / blocklist)",
        49 => "Registry is too large",
        52 => "Driver signature could not be verified",
        _ => $"Problem code {code}",
    };
}
