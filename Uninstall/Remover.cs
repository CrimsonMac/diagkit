using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using DiagKit.Kit;

namespace DiagKit.Uninstall;

public sealed class RemovalOptions
{
    /// <summary>Delete the kit's files too (otherwise only this PC is cleaned and the kit stays usable).</summary>
    public bool RemoveKit { get; set; } = true;

    /// <summary>Delete saved health-check reports (in the kit, and on this PC when the kit was read-only).</summary>
    public bool DeleteReports { get; set; } = true;

    /// <summary>Uninstall the PawnIO driver, when DiagKit installed it on this PC.</summary>
    public bool RemovePawnIo { get; set; } = true;
}

/// <summary>Works out what DiagKit put on this PC and in the kit, and removes exactly that.</summary>
public sealed class Remover
{
    private readonly Action<string> _log;

    public Remover(string kitDir, Action<string> log)
    {
        KitDir = Path.GetFullPath(kitDir).TrimEnd('\\');
        _log = log;
        Ledger = KitLedger.Load(KitDir);
    }

    public string KitDir { get; }
    public KitLedger Ledger { get; }

    // ------------------------------------------------------------------ what is there

    /// <summary>PawnIO is on this PC because DiagKit's health check installed it.</summary>
    public bool PawnIoInstalledByDiagKit => Ledger.TracesOnThisPc().Any(t => t.Kind == TraceKinds.PawnIo) && PawnIo.IsInstalled();

    public DateTime PawnIoInstallDate => Ledger.TracesOnThisPc().Where(t => t.Kind == TraceKinds.PawnIo).Select(t => t.Date).DefaultIfEmpty().Max();

    /// <summary>Registry keys, driver services and files a tool left on this PC that are still waiting to be removed.</summary>
    public List<PcTrace> PendingTraces => Ledger.TracesOnThisPc().Where(t => t.Kind is not (TraceKinds.PawnIo or TraceKinds.Shortcut)).ToList();

    /// <summary>The Start menu shortcut setup made for this kit on this PC, if any.</summary>
    public string? StartMenuShortcut => StartMenu.Existing(KitDir);

    /// <summary>Other PCs where DiagKit installed PawnIO or left something; cleaning them needs Uninstall run there.</summary>
    public List<string> OtherPcsWithTraces => Ledger.Traces
        .Where(t => !string.Equals(t.Machine, ThisMachine.Id, StringComparison.OrdinalIgnoreCase))
        .Select(t => t.PcName + (t.Kind == TraceKinds.PawnIo ? " (PawnIO driver)" : ""))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public string ReportsDir => Path.Combine(KitDir, KitLayout.ReportsFolder);

    /// <summary>Where DiagKit saves reports on this PC when the kit is read-only.</summary>
    public static string LocalReportsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DiagKit Reports");

    public int ReportCount => CountReports(ReportsDir) + CountReports(LocalReportsDir);

    private static int CountReports(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetDirectories(dir).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Tool folders DiagKit installed: recorded by setup, or stamped by fetch-tools.ps1 in kits made before 1.1.</summary>
    public List<string> InstalledToolFolders()
    {
        string tools = Path.Combine(KitDir, KitLayout.ToolsFolder);
        var result = new List<string>();
        foreach (ToolRecord t in Ledger.Tools)
        {
            string dir = Path.Combine(tools, t.Folder);
            if (Directory.Exists(dir))
            {
                result.Add(dir);
            }
        }

        if (Directory.Exists(tools))
        {
            foreach (string dir in Directory.GetDirectories(tools))
            {
                if (File.Exists(Path.Combine(dir, KitLayout.LegacyStamp)) && !result.Contains(dir, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(dir);
                }
            }
        }

        return result;
    }

    /// <summary>Single files DiagKit owns in the kit.</summary>
    private List<string> OwnFiles()
    {
        var names = new List<string>(Ledger.Files)
        {
            KitLayout.AppExe,
            KitLayout.UninstallExe,
            KitLayout.ErrorLog,
            KitLayout.SettingsFile,
            KitLayout.ToolList, // kits made before 1.1 kept it in the root
            KitLedger.FileName,
            KitLedger.FileName + ".tmp",
            Path.Combine(KitLayout.ToolsFolder, KitLayout.ToolList),
            Path.Combine(KitLayout.ToolsFolder, "README.md"), // copied there by build.ps1 before 1.1
        };
        return names.Select(n => Path.Combine(KitDir, n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Files in the kit that DiagKit did not put there; they are kept.</summary>
    public List<string> UserFiles(bool deleteReports)
    {
        var own = new HashSet<string>(OwnFiles(), StringComparer.OrdinalIgnoreCase);
        List<string> toolDirs = InstalledToolFolders();
        var result = new List<string>();
        foreach (string f in SafeFiles(KitDir))
        {
            bool ours = own.Contains(f) ||
                        toolDirs.Any(d => TraceCleaner.IsUnder(f, d)) ||
                        (deleteReports && TraceCleaner.IsUnder(f, ReportsDir));
            if (!ours)
            {
                result.Add(f.Substring(KitDir.Length + 1));
            }
        }

        return result;
    }

    /// <summary>Programs running from the kit (DiagKit or a tool), other than this uninstaller.</summary>
    public List<(int Id, string Path)> RunningFromKit()
    {
        var list = new List<(int, string)>();
        int self = Process.GetCurrentProcess().Id;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath FROM Win32_Process");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using (o)
                {
                    int id = Convert.ToInt32(o["ProcessId"]);
                    if (id != self && o["ExecutablePath"] is string path && TraceCleaner.IsUnder(path, KitDir))
                    {
                        list.Add((id, path));
                    }
                }
            }
        }
        catch
        {
            // WMI unavailable: deletion errors will tell.
        }

        return list;
    }

    public void CloseRunning()
    {
        foreach ((int id, string path) in RunningFromKit())
        {
            try
            {
                using Process p = Process.GetProcessById(id);
                p.Kill();
                p.WaitForExit(5000);
                _log("Closed " + Path.GetFileName(path));
            }
            catch
            {
                // Already gone.
            }
        }
    }

    // ------------------------------------------------------------------ removal

    /// <summary>Removes what DiagKit and its tools left on this PC. Returns the number of problems.</summary>
    public int CleanThisPc(RemovalOptions options)
    {
        int problems = 0;

        // 1. Keys, driver services and files tools left behind (normally removed when the tool closes).
        List<PcTrace> failed = TraceCleaner.CleanPending(KitDir, includePawnIo: options.RemovePawnIo, log: _log);
        problems += failed.Count;

        // 2. The Start menu shortcut, if setup was asked to make one.
        problems += StartMenu.Remove(KitDir, _log) ? 0 : 1;

        // 3. Windows' records of programs run from the kit.
        var scrubber = new WindowsTraces(KitFolderNames(), KitProgramNames(), _log);
        int records = scrubber.ScrubAll();
        if (records == 0)
        {
            _log("Windows had no records of programs run from the kit");
        }

        // 4. DiagKit's own leftovers in this PC's temp folder (and the runtime copy older DiagKit versions extracted).
        string temp = Path.GetTempPath();
        DeleteFile(Path.Combine(temp, KitLayout.ErrorLog));
        DeleteDir(Path.Combine(temp, ".net", "DiagKit"), "the runtime files an older DiagKit unpacked");
        int leftovers = 0;
        foreach (string pattern in new[] { "diagkit-setup-*", "diagkit-fetch-*", "diagkit-build-*" })
        {
            foreach (string dir in SafeDirs(temp, pattern))
            {
                leftovers += DeleteDir(dir, null) ? 1 : 0;
            }
        }

        if (leftovers > 0)
        {
            _log("Deleted leftover setup files from the temp folder");
        }

        if (options.DeleteReports)
        {
            DeleteDir(LocalReportsDir, "reports saved on this PC");
        }

        return problems;
    }

    /// <summary>Deletes the kit's own files and folders, keeping anything the user added. Returns the number of problems.</summary>
    public int RemoveKitFiles(RemovalOptions options)
    {
        int problems = 0;
        string self = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        foreach (string dir in InstalledToolFolders())
        {
            problems += DeleteDir(dir, null) ? 0 : 1;
        }

        if (options.DeleteReports)
        {
            problems += DeleteDir(ReportsDir, "saved reports") ? 0 : 1;
        }

        foreach (string f in OwnFiles().Where(f => !string.Equals(f, self, StringComparison.OrdinalIgnoreCase) &&
                                                     !f.EndsWith(KitLedger.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            problems += DeleteFile(f) ? 0 : 1;
        }

        RemoveEmptyDirs(Path.Combine(KitDir, KitLayout.ToolsFolder));
        _log("Removed DiagKit's files from the kit");

        // The ledger goes last: if anything above failed, running Uninstall again still knows what to remove.
        if (problems == 0)
        {
            DeleteFile(KitLedger.PathFor(KitDir));
        }

        return problems;
    }

    /// <summary>
    /// This program cannot delete itself while it runs, so a hidden command prompt does it a moment after it exits,
    /// then removes the kit folder if nothing else is left in it.
    /// </summary>
    public void ScheduleSelfDelete()
    {
        string self = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (self.Length == 0 || !TraceCleaner.IsUnder(self, KitDir))
        {
            return;
        }

        string cmd = $"/d /c ping -n 4 127.0.0.1 >nul & del /f /q \"{self}\" & rmdir \"{KitDir}\"";
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), cmd)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            })?.Dispose();
        }
        catch (Exception ex)
        {
            _log("Could not schedule removal of " + Path.GetFileName(self) + ": " + ex.Message);
        }
    }

    /// <summary>Prefetch only writes a program's file about ten seconds after it starts; this removes the uninstaller's own once it exists.</summary>
    public void ScrubOwnPrefetch()
    {
        TimeSpan running = DateTime.Now - Process.GetCurrentProcess().StartTime;
        TimeSpan wait = TimeSpan.FromSeconds(12) - running;
        if (wait > TimeSpan.Zero)
        {
            System.Threading.Thread.Sleep(wait);
        }

        new WindowsTraces(KitFolderNames(), new[] { KitLayout.UninstallExe }, _ => { }).ScrubPrefetch();
    }

    /// <summary>The kit folder relative to its drive, under its current name and every former one.</summary>
    private IEnumerable<string> KitFolderNames()
    {
        string root = Path.GetPathRoot(KitDir) ?? "";
        string rel = KitDir.Substring(root.Length);
        yield return rel;
        string parent = Path.GetDirectoryName(rel) ?? "";
        foreach (string former in Ledger.FormerNames)
        {
            // Former names are stored relative to the drive; older entries may hold just the folder name.
            yield return former.Contains('\\') ? former : Path.Combine(parent, former);
        }
    }

    private IEnumerable<string> KitProgramNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { KitLayout.AppExe, KitLayout.UninstallExe };
        foreach (string f in SafeFiles(KitDir).Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            names.Add(Path.GetFileName(f));
        }

        return names;
    }

    // ------------------------------------------------------------------ helpers

    private bool DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex)
        {
            _log($"Could not delete {path}: {ex.Message}");
            return false;
        }
    }

    private bool DeleteDir(string dir, string? what)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return true;
            }

            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(dir, recursive: true);
            if (what != null)
            {
                _log("Deleted " + what);
            }

            return true;
        }
        catch (Exception ex)
        {
            _log($"Could not delete {dir}: {ex.Message}");
            return false;
        }
    }

    private static void RemoveEmptyDirs(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (string sub in Directory.GetDirectories(dir))
            {
                RemoveEmptyDirs(sub);
            }

            if (Directory.GetFileSystemEntries(dir).Length == 0)
            {
                Directory.Delete(dir);
            }
        }
        catch
        {
            // Not empty or in use; keep it.
        }
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeDirs(string dir, string pattern)
    {
        try
        {
            return Directory.GetDirectories(dir, pattern);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
