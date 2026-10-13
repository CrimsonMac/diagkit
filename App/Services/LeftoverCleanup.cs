using System;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using DiagKit.Kit;

namespace DiagKit.Services;

/// <summary>
/// Finishes cleanups that could not run earlier: registry keys, driver services and temp files a tool left
/// because DiagKit was closed while the tool was still open, or because a driver was still loaded.
/// </summary>
public static class LeftoverCleanup
{
    public static Task RunAsync() => Task.Run(() =>
    {
        try
        {
            if (!KitLedger.Load(AppPaths.BaseDir).TracesOnThisPc().Any(t => t.Kind != TraceKinds.PawnIo))
            {
                return;
            }

            // A tool from this kit still running owns its keys and driver; try again next time.
            if (AnyKitToolRunning())
            {
                return;
            }

            TraceCleaner.CleanPending(AppPaths.BaseDir, includePawnIo: false);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("Cleaning up after earlier tool runs", ex);
        }
    });

    private static bool AnyKitToolRunning()
    {
        string tools = AppPaths.ToolsRoot;
        using var searcher = new ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process");
        foreach (ManagementBaseObject o in searcher.Get())
        {
            using (o)
            {
                if (o["ExecutablePath"] is string path && TraceCleaner.IsUnder(path, tools))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
