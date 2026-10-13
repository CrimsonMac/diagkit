using System;
using System.IO;
using System.Linq;
using System.Text;
using DiagKit.Models;

namespace DiagKit.Services;

/// <summary>Small repairs some portable tools need before each launch from a USB stick.</summary>
public static class ToolFixups
{
    public static void BeforeLaunch(ToolDefinition tool, string exePath)
    {
        try
        {
            if (string.Equals(tool.Id, "testmem5", StringComparison.OrdinalIgnoreCase))
            {
                RepairTestMem5ConfigLink(Path.GetDirectoryName(exePath) ?? "");
            }
        }
        catch (Exception ex)
        {
            // A failed repair only means the tool may start with its default settings.
            AppPaths.LogError("Preparing " + tool.Name, ex);
        }
    }

    /// <summary>
    /// TestMem5 remembers the loaded config as an absolute path in bin\Cfg.link. When the stick gets another drive
    /// letter that path is gone and TM5 silently falls back to its weak built-in test. Point the link at the same
    /// file in this copy's bin folder, or remove it so TM5 recreates it for bin\MT.cfg.
    /// </summary>
    internal static void RepairTestMem5ConfigLink(string tm5Dir)
    {
        string bin = Path.Combine(tm5Dir, "bin");
        string link = Path.Combine(bin, "Cfg.link");
        if (!File.Exists(link))
        {
            return;
        }

        // TM5 writes the path as ANSI bytes with no terminator or newline.
        string target = Compat.Latin1.GetString(File.ReadAllBytes(link)).Trim('\0', ' ', '\r', '\n');
        if (target.Length > 0 && File.Exists(target))
        {
            return;
        }

        string local = Path.Combine(bin, Path.GetFileName(target));
        bool ascii = local.All(c => c < 128);
        if (target.Length > 0 && File.Exists(local) && ascii)
        {
            File.WriteAllBytes(link, Encoding.ASCII.GetBytes(local));
        }
        else
        {
            File.Delete(link);
        }
    }
}
