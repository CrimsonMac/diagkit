using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DiagKit.Report.Collectors;

public sealed class StorageCollector : ICollector
{
    public string Name => "Drive health";
    public string Description => "SMART data: bad sectors, wear, temperature, error counters";
    public int Phase => 1;
    public TimeSpan Timeout => TimeSpan.FromSeconds(120);

    private const string StorageNs = @"root\Microsoft\Windows\Storage";

    private static readonly Regex SdName = new(@"^/dev/sd([a-z]+)$", RegexOptions.IgnoreCase);

    public async Task CollectAsync(CollectorContext ctx, CollectorStatus status, CancellationToken ct)
    {
        List<DiskInfo> disks = ReadWindowsDisks(out string? windowsError);
        // Mounted VHD/VHDX files (WSL, Sandbox, backups) are not hardware; leave them out of drive health.
        int virtualDisks = disks.RemoveAll(d => d.BusType == "Virtual");
        ct.ThrowIfCancellationRequested();

        string? smartctl = ctx.ToolPath("smartctl");
        int smartCount = 0;
        string? smartError = null;

        if (smartctl != null)
        {
            try
            {
                smartCount = await ReadSmartAsync(ctx, smartctl, disks, status, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                smartError = ex.Message;
            }
        }

        ctx.Report.Disks.AddRange(disks.OrderBy(d => d.Number ?? int.MaxValue));

        string summary = TextUtil.Plural(disks.Count, "drive") + " found" +
                         (virtualDisks > 0 ? $" ({TextUtil.Plural(virtualDisks, "virtual disk")} skipped)" : "");
        if (smartctl == null)
        {
            status.IsPartial = true;
            status.Detail = summary + ". smartctl not in the kit, so only Windows' own health flag was checked";
        }
        else if (smartError != null)
        {
            status.IsPartial = true;
            status.Detail = summary + ". smartctl failed: " + smartError;
        }
        else
        {
            status.Detail = $"{summary}, SMART read from {smartCount}";
        }

        if (windowsError != null)
        {
            status.IsPartial = true;
            status.Detail += " (Windows storage query failed)";
        }
    }

    private static List<DiskInfo> ReadWindowsDisks(out string? error)
    {
        error = null;
        var disks = new List<DiskInfo>();
        try
        {
            var media = new Dictionary<string, (string? Media, string? Health)>();
            foreach (var r in Wmi.Query("SELECT DeviceId, MediaType, HealthStatus FROM MSFT_PhysicalDisk", StorageNs))
            {
                string? id = r.Str("DeviceId");
                if (id == null)
                {
                    continue;
                }

                string? mediaType = (r.Int("MediaType") ?? 0) switch
                {
                    3 => "HDD",
                    4 => "SSD",
                    5 => "SCM",
                    _ => null,
                };
                media[id] = (mediaType, HealthText(r.Int("HealthStatus")));
            }

            foreach (var r in Wmi.Query("SELECT Number, SerialNumber, Model, FriendlyName, BusType, Size, HealthStatus FROM MSFT_Disk", StorageNs))
            {
                int? number = r.Int("Number");
                var disk = new DiskInfo
                {
                    Number = number,
                    Model = r.Str("FriendlyName") ?? r.Str("Model") ?? "Unknown drive",
                    Serial = r.Str("SerialNumber"),
                    BusType = BusText(r.Int("BusType")),
                    SizeBytes = r.Long("Size") ?? 0,
                    WindowsHealth = HealthText(r.Int("HealthStatus")),
                };

                if (number != null && media.TryGetValue(number.Value.ToString(), out var m))
                {
                    disk.MediaType = disk.BusType == "NVMe" ? "NVMe SSD" : m.Media;
                    disk.WindowsHealth = m.Health ?? disk.WindowsHealth;
                }
                else if (disk.BusType == "NVMe")
                {
                    disk.MediaType = "NVMe SSD";
                }

                disks.Add(disk);
            }

            foreach (var r in Wmi.Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", StorageNs))
            {
                int? n = r.Int("DiskNumber");
                string? letter = r.Str("DriveLetter");
                if (n == null || letter == null || !char.IsLetter(letter[0]))
                {
                    continue;
                }

                DiskInfo? d = disks.FirstOrDefault(x => x.Number == n);
                d?.DriveLetters.Add(letter[0] + ":");
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return disks;
    }

    private static async Task<int> ReadSmartAsync(CollectorContext ctx, string smartctl, List<DiskInfo> disks, CollectorStatus status, CancellationToken ct)
    {
        status.Detail = "Scanning drives with smartctl";
        ProcessResult scan = await ProcessRunner.RunAsync(smartctl, "--scan-open -j", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        string scanJson = TextUtil.Decode(scan.StdOut);
        ctx.WriteRaw("smartctl-scan.json", scanJson);

        var devices = new List<(string Name, string Type)>();
        using (JsonDocument doc = JsonDocument.Parse(scanJson))
        {
            if (doc.RootElement.TryGetProperty("devices", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement d in list.EnumerateArray())
                {
                    string? name = d.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                    string type = d.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(name))
                    {
                        devices.Add((name!, type));
                    }
                }
            }
        }

        int read = 0;
        int index = 0;
        foreach ((string name, string type) in devices)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            status.Detail = $"Reading SMART {index} of {devices.Count}";

            string args = (string.IsNullOrEmpty(type) ? "" : $"-d {type} ") + $"-a -j {name}";
            ProcessResult res = await ProcessRunner.RunAsync(smartctl, args, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (res.TimedOut)
            {
                continue;
            }

            string json = TextUtil.Decode(res.StdOut);
            string safeName = Regex.Replace(name, "[^A-Za-z0-9]+", "_").Trim('_');
            string rawFile = $"smartctl-{safeName}.json";
            ctx.WriteRaw(rawFile, json);

            DiskInfo? parsed;
            try
            {
                parsed = ParseSmart(json);
            }
            catch (JsonException)
            {
                // Empty or broken output for this device (unsupported USB bridge, smartctl crash); the raw file has it.
                continue;
            }

            if (parsed == null)
            {
                continue;
            }

            parsed.RawFile = rawFile;
            parsed.SmartExitStatus = res.ExitCode;
            Merge(parsed, name, disks);
            read++;
        }

        return read;
    }

    private static void Merge(DiskInfo smart, string deviceName, List<DiskInfo> disks)
    {
        DiskInfo? target = null;
        string? key = Normalize(smart.Serial);
        if (key != null)
        {
            target = disks.FirstOrDefault(d => Normalize(d.Serial) == key);
        }

        if (target == null)
        {
            Match m = SdName.Match(deviceName);
            if (m.Success)
            {
                int number = 0;
                foreach (char c in m.Groups[1].Value.ToLowerInvariant())
                {
                    number = number * 26 + (c - 'a' + 1);
                }

                number -= 1;
                target = disks.FirstOrDefault(d => d.Number == number && d.Source == "windows");
            }
        }

        if (target == null)
        {
            smart.Source = "smartctl";
            disks.Add(smart);
            return;
        }

        target.Source = "smartctl";
        target.Firmware = smart.Firmware;
        target.SmartPassed = smart.SmartPassed;
        target.SmartExitStatus = smart.SmartExitStatus;
        target.Temperature = smart.Temperature;
        target.PowerOnHours = smart.PowerOnHours;
        target.ReallocatedSectors = smart.ReallocatedSectors;
        target.PendingSectors = smart.PendingSectors;
        target.UncorrectableSectors = smart.UncorrectableSectors;
        target.ReportedUncorrectable = smart.ReportedUncorrectable;
        target.CrcErrors = smart.CrcErrors;
        target.LifeLeftPercent = smart.LifeLeftPercent;
        target.PercentageUsed = smart.PercentageUsed;
        target.AvailableSpare = smart.AvailableSpare;
        target.AvailableSpareThreshold = smart.AvailableSpareThreshold;
        target.MediaErrors = smart.MediaErrors;
        target.CriticalWarning = smart.CriticalWarning;
        target.UnsafeShutdowns = smart.UnsafeShutdowns;
        target.RawFile = smart.RawFile;
        if (target.MediaType == null)
        {
            target.MediaType = smart.MediaType;
        }

        if (string.IsNullOrWhiteSpace(target.Model) || target.Model == "Unknown drive")
        {
            target.Model = smart.Model;
        }
    }

    private static string? Normalize(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (char c in serial!)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    internal static DiskInfo? ParseSmart(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        var d = new DiskInfo
        {
            Model = Str(root, "model_name") ?? Str(root, "model_family") ?? "Unknown drive",
            Serial = Str(root, "serial_number"),
            Firmware = Str(root, "firmware_version"),
        };

        if (root.TryGetProperty("user_capacity", out JsonElement cap) && cap.TryGetProperty("bytes", out JsonElement capBytes) && capBytes.TryGetInt64(out long bytes))
        {
            d.SizeBytes = bytes;
        }

        if (root.TryGetProperty("smart_status", out JsonElement ss) && ss.TryGetProperty("passed", out JsonElement passed) &&
            passed.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            d.SmartPassed = passed.GetBoolean();
        }

        if (root.TryGetProperty("temperature", out JsonElement temp) && temp.TryGetProperty("current", out JsonElement cur) && cur.TryGetInt32(out int c))
        {
            d.Temperature = c;
        }

        if (root.TryGetProperty("power_on_time", out JsonElement pot) && pot.TryGetProperty("hours", out JsonElement h) && h.TryGetInt64(out long hours))
        {
            d.PowerOnHours = hours;
        }

        bool hasNvme = root.TryGetProperty("nvme_smart_health_information_log", out JsonElement nvme);
        if (hasNvme)
        {
            d.MediaType = "NVMe SSD";
            d.CriticalWarning = Int(nvme, "critical_warning");
            d.AvailableSpare = Int(nvme, "available_spare");
            d.AvailableSpareThreshold = Int(nvme, "available_spare_threshold");
            d.PercentageUsed = Int(nvme, "percentage_used");
            d.MediaErrors = Long(nvme, "media_errors");
            d.UnsafeShutdowns = Long(nvme, "unsafe_shutdowns");
            d.Temperature ??= Int(nvme, "temperature");
            d.PowerOnHours ??= Long(nvme, "power_on_hours");
        }

        if (root.TryGetProperty("rotation_rate", out JsonElement rot) && rot.TryGetInt32(out int rpm))
        {
            d.MediaType ??= rpm > 0 ? "HDD" : "SSD";
        }

        if (root.TryGetProperty("ata_smart_attributes", out JsonElement ata) && ata.TryGetProperty("table", out JsonElement table) &&
            table.ValueKind == JsonValueKind.Array)
        {
            var raw = new Dictionary<int, long>();
            var normalized = new Dictionary<int, int>();
            foreach (JsonElement a in table.EnumerateArray())
            {
                int? id = Int(a, "id");
                if (id == null)
                {
                    continue;
                }

                if (a.TryGetProperty("raw", out JsonElement r) && r.TryGetProperty("value", out JsonElement rv) && rv.TryGetInt64(out long rawValue))
                {
                    raw[id.Value] = rawValue;
                }

                if (Int(a, "value") is int nv)
                {
                    normalized[id.Value] = nv;
                }
            }

            d.ReallocatedSectors = Get(raw, 5);
            d.ReportedUncorrectable = Get(raw, 187);
            d.PendingSectors = Get(raw, 197);
            d.UncorrectableSectors = Get(raw, 198);
            d.CrcErrors = Get(raw, 199);

            // Remaining-life attributes differ per vendor; the normalized value is the percentage left.
            foreach (int lifeId in new[] { 231, 177, 202, 233, 169 })
            {
                if (normalized.TryGetValue(lifeId, out int left) && left is >= 0 and <= 100)
                {
                    d.LifeLeftPercent = left;
                    break;
                }
            }
        }

        return d;
    }

    private static long? Get(Dictionary<int, long> map, int id) => map.TryGetValue(id, out long v) ? (v & 0xFFFFFFFFFFFF) : null;

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;

    private static long? Long(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : null;

    private static string? HealthText(int? code) => code switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => null,
    };

    private static string? BusText(int? code) => code switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        15 => "Virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        _ => null,
    };
}
