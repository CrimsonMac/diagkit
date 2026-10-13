using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DiagKit.Kit;

namespace DiagKit.Setup;

/// <summary>Where setup will put the kit, worked out from the folder the user picked.</summary>
public sealed class KitTarget
{
    public KitTarget(string kitDir, bool existing)
    {
        KitDir = kitDir;
        Existing = existing;
    }

    /// <summary>The kit folder (it may not exist yet).</summary>
    public string KitDir { get; }

    /// <summary>True when an existing kit is being updated.</summary>
    public bool Existing { get; }

    /// <summary>
    /// The picked folder if it is a kit; otherwise the newest kit directly inside it; otherwise a new
    /// diagkit.&lt;version&gt; folder inside it.
    /// </summary>
    public static KitTarget For(string picked)
    {
        string dir = Path.GetFullPath(picked).TrimEnd('\\');
        if (dir.Length == 2 && dir[1] == ':')
        {
            dir += "\\";
        }

        if (KitLayout.IsKit(dir))
        {
            return new KitTarget(dir, true);
        }

        string[] kits = KitLayout.FindKits(dir);
        return kits.Length > 0
            ? new KitTarget(kits[0], true)
            : new KitTarget(Path.Combine(dir, KitLayout.FolderName(SetupInfo.Version)), false);
    }
}

public enum ToolState
{
    Waiting,
    Working,
    Done,
    Kept,
    Failed,
    Removed,
}

public sealed class ToolProgress
{
    public ToolProgress(ToolSpec tool, ToolState state, string detail)
    {
        Tool = tool;
        State = state;
        Detail = detail;
    }

    public ToolSpec Tool { get; }
    public ToolState State { get; }
    public string Detail { get; }
}

public sealed class InstallSummary
{
    public string KitDir { get; set; } = "";
    public int Failed { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Creates or updates a kit: copies DiagKit.exe and Uninstall.exe, downloads the selected tools, and records all of
/// it in the kit ledger so Uninstall.exe knows exactly what is DiagKit's. A tool folder that is already in the kit but
/// was not put there by DiagKit (a copy the user added by hand) is left alone.
/// </summary>
public sealed class KitInstaller
{
    private const int Parallel = 3;
    private readonly KitTarget _target;
    private readonly List<ToolSpec> _selected;
    private readonly bool _force;
    private readonly Action<ToolProgress> _progress;
    private readonly Action<string> _log;

    public KitInstaller(KitTarget target, IEnumerable<ToolSpec> selected, bool force, Action<ToolProgress> progress, Action<string> log)
    {
        _target = target;
        _selected = selected.ToList();
        _force = force;
        _progress = progress;
        _log = log;
    }

    /// <summary>Tool folders an existing kit already has from DiagKit (recorded in its ledger, or stamped by fetch-tools.ps1).</summary>
    public static HashSet<string> InstalledFolders(string kitDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(kitDir))
        {
            return set;
        }

        foreach (ToolRecord t in KitLedger.Load(kitDir).Tools)
        {
            set.Add(t.Folder);
        }

        string tools = Path.Combine(kitDir, KitLayout.ToolsFolder);
        if (Directory.Exists(tools))
        {
            foreach (string d in Directory.GetDirectories(tools).Where(d => File.Exists(Path.Combine(d, KitLayout.LegacyStamp))))
            {
                set.Add(Path.GetFileName(d));
            }
        }

        return set;
    }

    public async Task<InstallSummary> RunAsync(CancellationToken ct)
    {
        var summary = new InstallSummary();
        string kit = _target.KitDir;
        Directory.CreateDirectory(kit);

        List<string> running = RunningFrom(kit);
        if (running.Count > 0)
        {
            throw new InvalidOperationException(string.Join(", ", running) + " is running from the kit. Close it and run setup again.");
        }

        InstallApp(kit);
        MigrateLegacyLayout(kit);

        string tools = Path.Combine(kit, KitLayout.ToolsFolder);
        Directory.CreateDirectory(tools);
        string work = Path.Combine(Path.GetTempPath(), "diagkit-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var fetcher = new Fetcher(work) { Force = _force };
            HashSet<string> installed = InstalledFolders(kit);
            using var gate = new SemaphoreSlim(Parallel);
            IEnumerable<Task> jobs = _selected.Select(async t =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    bool ok = await InstallToolAsync(fetcher, t, Path.Combine(tools, t.Folder), installed.Contains(t.Folder), ct).ConfigureAwait(false);
                    if (!ok)
                    {
                        Interlocked.Increment(ref _failed);
                    }
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(jobs).ConfigureAwait(false);

            KitLedger.Update(kit, l =>
            {
                l.Unwanted.Clear();
                l.Unwanted.AddRange(Catalog.All.Where(t => !_selected.Contains(t)).Select(t => t.Folder));
            });

            // Tools DiagKit installed earlier that are no longer wanted.
            foreach (ToolSpec t in Catalog.All.Where(t => !_selected.Contains(t) && installed.Contains(t.Folder)))
            {
                RemoveTool(kit, t);
            }
        }
        finally
        {
            TryDeleteDir(work);
        }

        summary.Failed = _failed;
        summary.KitDir = RenameForVersion(kit, summary);
        return summary;
    }

    private int _failed;

    private async Task<bool> InstallToolAsync(Fetcher fetcher, ToolSpec t, string dest, bool ours, CancellationToken ct)
    {
        if (!ours && Fetcher.HasFiles(dest))
        {
            // Not DiagKit's: someone put their own copy here. Leave it, and leave it out of the ledger.
            _progress(new ToolProgress(t, ToolState.Kept, "kept the copy you added yourself"));
            return true;
        }

        _progress(new ToolProgress(t, ToolState.Working, "checking for the latest version"));
        string kit = _target.KitDir;
        string current = KitLedger.Load(kit).FindTool(t.Folder)?.Stamp ?? ReadLegacyStamp(dest);
        ToolResult r = await fetcher.UpdateAsync(t, dest, current, line => _progress(new ToolProgress(t, ToolState.Working, line)), ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            _progress(new ToolProgress(t, ToolState.Failed, r.Detail));
            return false;
        }

        KitLedger.Update(kit, l => l.SetTool(t.Folder, r.Stamp ?? current));
        TryDeleteFile(Path.Combine(dest, KitLayout.LegacyStamp));
        _progress(new ToolProgress(t, ToolState.Done, r.State + " · " + r.Detail));
        return true;
    }

    private void RemoveTool(string kit, ToolSpec t)
    {
        string dest = Path.Combine(kit, KitLayout.ToolsFolder, t.Folder);
        if (TryDeleteDir(dest))
        {
            KitLedger.Update(kit, l => l.Tools.RemoveAll(x => string.Equals(x.Folder, t.Folder, StringComparison.OrdinalIgnoreCase)));
            _progress(new ToolProgress(t, ToolState.Removed, "removed from the kit"));
        }
        else
        {
            _progress(new ToolProgress(t, ToolState.Failed, "could not be removed; close it and run setup again"));
        }
    }

    /// <summary>Writes DiagKit.exe and Uninstall.exe (skipping identical copies) and records them in the ledger.</summary>
    private void InstallApp(string kit)
    {
        foreach (string name in new[] { KitLayout.AppExe, KitLayout.UninstallExe })
        {
            string target = Path.Combine(kit, name);
            byte[] payload = Payload.Read(name);
            if (File.Exists(target) && new FileInfo(target).Length == payload.Length && File.ReadAllBytes(target).SequenceEqual(payload))
            {
                continue;
            }

            if (File.Exists(target))
            {
                File.SetAttributes(target, FileAttributes.Normal);
            }

            File.WriteAllBytes(target, payload);
        }

        if (!KitLedger.Update(kit, l =>
            {
                l.Version = SetupInfo.Version;
                if (!l.Files.Contains(KitLayout.AppExe, StringComparer.OrdinalIgnoreCase))
                {
                    l.Files.Add(KitLayout.AppExe);
                }

                if (!l.Files.Contains(KitLayout.UninstallExe, StringComparer.OrdinalIgnoreCase))
                {
                    l.Files.Add(KitLayout.UninstallExe);
                }
            }))
        {
            throw new IOException("Could not write to the kit folder. Is it read-only?");
        }

        _log($"DiagKit {SetupInfo.Version} copied to the kit");
    }

    /// <summary>Kits made by build.ps1 before 1.1 kept tools.json in the root and a copy of TOOLS.md in tools\.</summary>
    private static void MigrateLegacyLayout(string kit)
    {
        string oldList = Path.Combine(kit, KitLayout.ToolList);
        string newList = Path.Combine(kit, KitLayout.ToolsFolder, KitLayout.ToolList);
        try
        {
            if (File.Exists(oldList) && !File.Exists(newList))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newList)!);
                File.Move(oldList, newList);
            }
        }
        catch
        {
            // DiagKit moves it on its next start instead.
        }

        TryDeleteFile(Path.Combine(kit, KitLayout.ToolsFolder, "README.md"));
        FixCase(kit, KitLayout.ToolsFolder);
        FixCase(kit, KitLayout.ReportsFolder);
    }

    /// <summary>"tools" to "Tools": Windows only changes a name's case through a rename via another name.</summary>
    private static void FixCase(string kit, string name)
    {
        try
        {
            string? actual = Directory.GetDirectories(kit).FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
            if (actual == null || Path.GetFileName(actual) == name)
            {
                return;
            }

            string temp = Path.Combine(kit, name + ".renaming");
            Directory.Move(actual, temp);
            Directory.Move(temp, Path.Combine(kit, name));
        }
        catch
        {
            // In use; the old spelling works just as well.
        }
    }

    /// <summary>
    /// Renames diagkit.1.0.0 (or a "DiagKit" folder from build.ps1) to diagkit.&lt;this version&gt;, remembering the old
    /// name so Uninstall can still find Windows' records of programs run from it. Returns the kit folder afterwards.
    /// </summary>
    private string RenameForVersion(string kit, InstallSummary summary)
    {
        string name = Path.GetFileName(kit);
        string wanted = KitLayout.FolderName(SetupInfo.Version);
        bool ours = name.StartsWith("diagkit.", StringComparison.OrdinalIgnoreCase) || name.Equals("DiagKit", StringComparison.OrdinalIgnoreCase);
        if (!ours || name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
        {
            return kit;
        }

        string parent = Path.GetDirectoryName(kit)!;
        string target = Path.Combine(parent, wanted);
        if (Directory.Exists(target))
        {
            summary.Notes.Add($"{wanted} already exists next to the kit, so the kit keeps its name {name}.");
            return kit;
        }

        string root = Path.GetPathRoot(kit) ?? "";
        KitLedger.Update(kit, l => l.FormerNames.Add(kit.Substring(root.Length)));
        try
        {
            Directory.Move(kit, target);
            _log($"Renamed the kit folder from {name} to {wanted}");
            return target;
        }
        catch (Exception ex)
        {
            summary.Notes.Add($"The kit folder could not be renamed to {wanted} ({ex.Message.TrimEnd('.')}); it works under its old name too.");
            return kit;
        }
    }

    private static string ReadLegacyStamp(string dest)
    {
        try
        {
            string f = Path.Combine(dest, KitLayout.LegacyStamp);
            return File.Exists(f) ? File.ReadAllText(f).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Programs running from inside the kit; their files cannot be replaced.</summary>
    private static List<string> RunningFrom(string kit)
    {
        var names = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using (o)
                {
                    if (o["ExecutablePath"] is string path && TraceCleaner.IsUnder(path, kit))
                    {
                        names.Add(Path.GetFileName(path));
                    }
                }
            }
        }
        catch
        {
            // WMI unavailable: a locked file will report itself.
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(dir, true);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Harmless leftover.
        }
    }
}

/// <summary>DiagKit.exe and Uninstall.exe, embedded in setup.exe at build time.</summary>
public static class Payload
{
    public static byte[] Read(string name)
    {
        using Stream s = typeof(Payload).Assembly.GetManifestResourceStream("payload/" + name)
                         ?? throw new InvalidOperationException(name + " is missing from setup.exe. Rebuild it with 'dotnet build Setup -c Release'.");
        var bytes = new byte[s.Length];
        int read = 0;
        while (read < bytes.Length)
        {
            int n = s.Read(bytes, read, bytes.Length - read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return bytes;
    }
}

public static class SetupInfo
{
    public static string Version { get; } = typeof(SetupInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
