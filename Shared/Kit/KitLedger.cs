using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace DiagKit.Kit;

/// <summary>
/// The kit's record of everything DiagKit itself put somewhere: the files and tool folders setup installed
/// in the kit, and, per PC, the traces DiagKit or its tools left on that PC (the PawnIO driver it installed,
/// registry keys and driver services a tool created that are not cleaned up yet).
/// Uninstall removes exactly what is listed here, so tools and settings that were already on a PC, or that
/// the user added to the kit by hand, are never touched.
/// Stored as a hidden, tab-separated text file in the kit root so every DiagKit program can read it
/// without a JSON library.
/// </summary>
public sealed class KitLedger
{
    public const string FileName = ".diagkit";
    private const string Header = "# DiagKit ledger: what DiagKit installed, in this kit and on each PC. Used by Uninstall.exe; do not edit.";

    public string KitDir { get; }

    /// <summary>DiagKit version that last set up this kit.</summary>
    public string? Version { get; set; }

    /// <summary>Files setup copied into the kit root (DiagKit.exe, Uninstall.exe), relative to the kit.</summary>
    public List<string> Files { get; } = new();

    /// <summary>Tool folders setup created under Tools\, with the source stamp of the installed version.</summary>
    public List<ToolRecord> Tools { get; } = new();

    /// <summary>Traces on PCs. Only entries for the current PC can be cleaned from here.</summary>
    public List<PcTrace> Traces { get; } = new();

    /// <summary>
    /// Earlier names of the kit folder (relative to its drive, e.g. "diagkit.1.0.0"), kept when setup renames the
    /// kit for a new version, so Uninstall can also find Windows' records of programs run from the old name.
    /// </summary>
    public List<string> FormerNames { get; } = new();

    private KitLedger(string kitDir)
    {
        KitDir = kitDir;
    }

    public static string PathFor(string kitDir) => Path.Combine(kitDir, FileName);

    public static bool Exists(string kitDir) => File.Exists(PathFor(kitDir));

    public static KitLedger Load(string kitDir)
    {
        var ledger = new KitLedger(kitDir);
        string path = PathFor(kitDir);
        if (!File.Exists(path))
        {
            return ledger;
        }

        foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] f = line.Split('\t');
            switch (f[0])
            {
                case "version" when f.Length > 1:
                    ledger.Version = f[1];
                    break;
                case "file" when f.Length > 1:
                    ledger.Files.Add(f[1]);
                    break;
                case "formername" when f.Length > 1:
                    ledger.FormerNames.Add(f[1]);
                    break;
                case "tool" when f.Length > 2:
                    ledger.Tools.Add(new ToolRecord(f[1], f[2]));
                    break;
                case "pc" when f.Length > 4:
                    ledger.Traces.Add(new PcTrace(f[1], f[2], f[3], f[4], f.Length > 5 ? f[5] : "", f.Length > 6 ? ParseDate(f[6]) : DateTime.MinValue));
                    break;
            }
        }

        return ledger;
    }

    public void Save()
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append("\r\n");
        if (!string.IsNullOrEmpty(Version))
        {
            sb.Append("version\t").Append(Version).Append("\r\n");
        }

        foreach (string f in Files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("file\t").Append(Clean(f)).Append("\r\n");
        }

        foreach (string n in FormerNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("formername\t").Append(Clean(n)).Append("\r\n");
        }

        foreach (ToolRecord t in Tools)
        {
            sb.Append("tool\t").Append(Clean(t.Folder)).Append('\t').Append(Clean(t.Stamp)).Append("\r\n");
        }

        foreach (PcTrace t in Traces)
        {
            sb.Append("pc\t").Append(Clean(t.Machine)).Append('\t').Append(Clean(t.PcName)).Append('\t').Append(Clean(t.Kind))
                .Append('\t').Append(Clean(t.Value)).Append('\t').Append(Clean(t.Detail)).Append('\t')
                .Append(t.Date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n");
        }

        string path = PathFor(KitDir);
        string temp = path + ".tmp";
        if (File.Exists(path))
        {
            // Hidden files cannot be overwritten by File.Replace's target rename on some file systems; clear it first.
            File.SetAttributes(path, FileAttributes.Normal);
        }

        File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }

        File.SetAttributes(path, FileAttributes.Hidden);
    }

    /// <summary>
    /// Loads, changes and saves the ledger while holding a lock, so DiagKit, setup and uninstall running at the
    /// same time cannot overwrite each other's entries. Returns false when the kit is read-only or locked.
    /// </summary>
    public static bool Update(string kitDir, Action<KitLedger> change)
    {
        using var mutex = new Mutex(false, @"Local\DiagKit.Ledger");
        bool owned = false;
        try
        {
            try
            {
                owned = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            KitLedger ledger = Load(kitDir);
            change(ledger);
            ledger.Save();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (owned)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    public ToolRecord? FindTool(string folder)
        => Tools.FirstOrDefault(t => string.Equals(t.Folder, folder, StringComparison.OrdinalIgnoreCase));

    public void SetTool(string folder, string stamp)
    {
        Tools.RemoveAll(t => string.Equals(t.Folder, folder, StringComparison.OrdinalIgnoreCase));
        Tools.Add(new ToolRecord(folder, stamp));
    }

    public IEnumerable<PcTrace> TracesOnThisPc()
        => Traces.Where(t => string.Equals(t.Machine, ThisMachine.Id, StringComparison.OrdinalIgnoreCase));

    public void AddTrace(string kind, string value, string detail = "")
    {
        RemoveTrace(kind, value);
        Traces.Add(new PcTrace(ThisMachine.Id, ThisMachine.Name, kind, value, detail, DateTime.Now));
    }

    public void RemoveTrace(string kind, string value)
        => Traces.RemoveAll(t => string.Equals(t.Machine, ThisMachine.Id, StringComparison.OrdinalIgnoreCase) &&
                                 t.Kind == kind && string.Equals(t.Value, value, StringComparison.OrdinalIgnoreCase));

    private static string Clean(string? s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static DateTime ParseDate(string s)
        => DateTime.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d) ? d : DateTime.MinValue;
}

public sealed class ToolRecord
{
    public ToolRecord(string folder, string stamp)
    {
        Folder = folder;
        Stamp = stamp;
    }

    /// <summary>Folder under Tools\, e.g. "GPU-Z".</summary>
    public string Folder { get; }

    /// <summary>Which release is installed ("scoop:2.66.0", "github:v2.2.0", ...), so setup only downloads changes.</summary>
    public string Stamp { get; set; }
}

/// <summary>Something DiagKit left on one PC.</summary>
public sealed class PcTrace
{
    public PcTrace(string machine, string pcName, string kind, string value, string detail, DateTime date)
    {
        Machine = machine;
        PcName = pcName;
        Kind = kind;
        Value = value;
        Detail = detail;
        Date = date;
    }

    public string Machine { get; }
    public string PcName { get; }

    /// <summary>One of <see cref="TraceKinds"/>.</summary>
    public string Kind { get; }

    /// <summary>Registry key, service name or file path.</summary>
    public string Value { get; }

    /// <summary>Extra information, such as the tool that created it or a driver file path.</summary>
    public string Detail { get; }

    public DateTime Date { get; }
}

public static class TraceKinds
{
    /// <summary>DiagKit installed the PawnIO driver on this PC (it was not there before).</summary>
    public const string PawnIo = "pawnio";

    /// <summary>A registry key a tool created that has not been deleted yet (the tool was still running, or DiagKit closed first).</summary>
    public const string RegistryKey = "regkey";

    /// <summary>A driver service a tool created that could not be removed yet. Detail holds the driver file, if DiagKit may delete it.</summary>
    public const string Service = "service";

    /// <summary>A file a tool left outside the kit (in %TEMP%, for example).</summary>
    public const string File = "file";
}

/// <summary>Identifies the current PC across runs, so a kit used on many PCs keeps their traces apart.</summary>
public static class ThisMachine
{
    public static string Id { get; } = ReadMachineGuid();

    public static string Name => Environment.MachineName;

    private static string ReadMachineGuid()
    {
        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string g && g.Length > 0)
            {
                return g;
            }
        }
        catch
        {
            // Fall through to the computer name.
        }

        return "name:" + Environment.MachineName;
    }
}
