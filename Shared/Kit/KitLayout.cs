using System;
using System.IO;
using System.Linq;

namespace DiagKit.Kit;

/// <summary>
/// Names of the files and folders in a kit, shared by DiagKit, setup and uninstall:
/// <code>
/// diagkit.&lt;version&gt;\
///   DiagKit.exe        the app
///   Uninstall.exe      removes DiagKit from this PC and/or deletes the kit
///   Tools\             one folder per tool, plus tools.json
///   Reports\           saved health checks
///   .diagkit           hidden ledger (see KitLedger)
/// </code>
/// </summary>
public static class KitLayout
{
    public const string AppExe = "DiagKit.exe";
    public const string UninstallExe = "Uninstall.exe";
    public const string ToolsFolder = "Tools";
    public const string ReportsFolder = "Reports";
    public const string ToolList = "tools.json";
    public const string ErrorLog = "diagkit-error.log";

    /// <summary>Per-tool stamp file written by fetch-tools.ps1 before 1.1; marks a folder DiagKit downloaded.</summary>
    public const string LegacyStamp = ".diagkit-source";

    /// <summary>Kit folder name for a version: "diagkit.1.1.0".</summary>
    public static string FolderName(string version) => "diagkit." + version;

    /// <summary>True when the folder holds a kit (current layout, or one built by build.ps1 before 1.1).</summary>
    public static bool IsKit(string dir)
    {
        try
        {
            return Directory.Exists(dir) &&
                   (KitLedger.Exists(dir) || File.Exists(Path.Combine(dir, AppExe)));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Kits directly inside <paramref name="parent"/>: diagkit.* folders, and "DiagKit" from older builds.</summary>
    public static string[] FindKits(string parent)
    {
        try
        {
            return Directory.GetDirectories(parent)
                .Where(d =>
                {
                    string name = Path.GetFileName(d);
                    return (name.StartsWith("diagkit.", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("DiagKit", StringComparison.OrdinalIgnoreCase)) && IsKit(d);
                })
                .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
