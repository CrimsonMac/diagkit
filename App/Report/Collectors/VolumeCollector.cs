using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DiagKit.Report.Collectors;

public sealed class VolumeCollector : ICollector
{
    public string Name => "Free space";
    public string Description => "Space left on every internal drive";
    public int Phase => 0;
    public TimeSpan Timeout => TimeSpan.FromSeconds(20);

    public Task CollectAsync(CollectorContext ctx, CollectorStatus status, CancellationToken ct)
    {
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady)
                {
                    continue;
                }

                ctx.Report.Volumes.Add(new VolumeInfo
                {
                    Letter = d.Name.TrimEnd('\\'),
                    Label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel,
                    FileSystem = d.DriveFormat,
                    TotalBytes = d.TotalSize,
                    FreeBytes = d.TotalFreeSpace,
                    IsSystem = string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase),
                });
            }
            catch (IOException)
            {
                // BitLocker-locked or disappearing volume.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        VolumeInfo? sys = ctx.Report.Volumes.FirstOrDefault(v => v.IsSystem);
        status.Detail = sys != null
            ? $"{sys.Letter} has {TextUtil.Bytes(sys.FreeBytes)} free of {TextUtil.Bytes(sys.TotalBytes)}"
            : TextUtil.Plural(ctx.Report.Volumes.Count, "volume") + " checked";
        return Task.CompletedTask;
    }
}
