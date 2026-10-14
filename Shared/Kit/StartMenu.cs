using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DiagKit.Kit;

/// <summary>
/// The optional "DiagKit" shortcut in the current user's Start menu. Windows Search only finds files in indexed
/// places (the user's folders and the Start menu), so a kit on another drive or a USB stick never shows up when
/// searching for DiagKit; a Start menu shortcut is how apps appear there. Setup creates it only when asked, records
/// it in the kit's ledger as a trace on this PC, and Uninstall removes it. A shortcut of the same name that DiagKit
/// did not create is never overwritten or deleted.
/// </summary>
public static class StartMenu
{
    public const string ShortcutName = "DiagKit.lnk";

    public static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName);

    /// <summary>The shortcut this kit created on this PC, if it still exists.</summary>
    public static string? Existing(string kitDir)
        => KitLedger.Load(kitDir).TracesOnThisPc().Where(t => t.Kind == TraceKinds.Shortcut).Select(t => t.Value).FirstOrDefault(File.Exists);

    public static bool IsRecorded(string kitDir)
        => KitLedger.Load(kitDir).TracesOnThisPc().Any(t => t.Kind == TraceKinds.Shortcut);

    /// <summary>Creates or refreshes the shortcut (after a kit rename its target moves). Returns a line for the log.</summary>
    public static string Add(string kitDir)
    {
        string path = ShortcutPath;
        string exe = Path.Combine(kitDir, KitLayout.AppExe);
        bool ours = KitLedger.Load(kitDir).TracesOnThisPc().Any(t => t.Kind == TraceKinds.Shortcut &&
                                                                      string.Equals(t.Value, path, StringComparison.OrdinalIgnoreCase));
        if (File.Exists(path) && !ours)
        {
            return $"Not added to the Start menu: there is already a {ShortcutName} there that this kit did not create (another kit's, perhaps).";
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(exe);
            link.SetWorkingDirectory(kitDir);
            link.SetDescription("DiagKit: PC diagnostics kit");
            link.SetIconLocation(exe, 0);
            ((IPersistFile)link).Save(path, true);
            Marshal.ReleaseComObject(link);
        }
        catch (Exception ex)
        {
            return "Could not add DiagKit to the Start menu: " + ex.Message;
        }

        KitLedger.Update(kitDir, l => l.AddTrace(TraceKinds.Shortcut, path, exe));
        return ours ? "Start menu shortcut updated" : "Added DiagKit to the Start menu, so Windows search finds it";
    }

    /// <summary>Deletes the shortcuts this kit created on this PC. Returns false if one could not be deleted.</summary>
    public static bool Remove(string kitDir, Action<string> log)
    {
        bool ok = true;
        foreach (PcTrace t in KitLedger.Load(kitDir).TracesOnThisPc().Where(t => t.Kind == TraceKinds.Shortcut).ToList())
        {
            try
            {
                // Only while it still points at a DiagKit.exe: if the user re-aimed it at something else, it is theirs now.
                string? target = File.Exists(t.Value) ? Target(t.Value) : null;
                if (target == null || string.Equals(Path.GetFileName(target), KitLayout.AppExe, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(t.Value))
                    {
                        File.Delete(t.Value);
                        log("Removed the DiagKit shortcut from the Start menu");
                    }
                }

                KitLedger.Update(kitDir, l => l.RemoveTrace(TraceKinds.Shortcut, t.Value));
            }
            catch (Exception ex)
            {
                log("Could not remove the Start menu shortcut: " + ex.Message);
                ok = false;
            }
        }

        return ok;
    }

    private static string? Target(string lnk)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(lnk, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.Length > 0 ? sb.ToString() : null;
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
