using System;
using System.Threading;
using System.Threading.Tasks;
using DiagKit.Services;

namespace DiagKit.Report.Collectors;

/// <summary>
/// Installs the signed PawnIO driver before the sensors are read, so CPU temperatures and clocks are available.
/// Only planned when the user opted in, PawnIO is missing, and its setup is in the kit.
/// </summary>
public sealed class PawnIoSetupCollector : ICollector
{
    public string Name => "Sensor driver";
    public string Description => "Installs PawnIO, the signed driver CPU temperature readings need";
    public int Phase => -1;
    public TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public async Task CollectAsync(CollectorContext ctx, CollectorStatus status, CancellationToken ct)
    {
        if (SystemProbe.IsPawnIoInstalled())
        {
            status.Detail = "PawnIO is already installed";
            return;
        }

        string? setup = ctx.ToolPath("pawnio");
        if (setup == null)
        {
            status.IsPartial = true;
            status.Detail = "PawnIO setup is not in the kit (setup.exe downloads it)";
            return;
        }

        status.Detail = "Installing PawnIO";
        ProcessResult res = await ProcessRunner.RunAsync(setup, "-install -silent", Timeout - TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (SystemProbe.IsPawnIoInstalled())
        {
            // PawnIO was not on this PC before, so Uninstall may remove it again.
            Kit.KitLedger.Update(AppPaths.BaseDir, l => l.AddTrace(Kit.TraceKinds.PawnIo, "PawnIO", "installed by the health check"));
            status.Detail = "Installed PawnIO for CPU temperatures";
            return;
        }

        status.IsPartial = true;
        status.Detail = res.TimedOut
            ? "PawnIO setup did not finish in time"
            : $"PawnIO setup failed (exit code {res.ExitCode}). Memory Integrity or antivirus may have blocked the driver";
    }
}
