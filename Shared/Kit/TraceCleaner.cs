using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace DiagKit.Kit;

/// <summary>
/// Removes single traces (registry keys, driver services, files) and the pending ones recorded in the ledger.
/// Every method is safe to call when the trace is already gone, and none of them throws.
/// </summary>
public static class TraceCleaner
{
    /// <summary>"HKCU\Software\X" to a root key and subkey path; null for hives DiagKit never touches.</summary>
    public static bool TrySplitKey(string key, out RegistryKey root, out string subKey)
    {
        root = Registry.CurrentUser;
        subKey = "";
        int slash = key.IndexOf('\\');
        if (slash <= 0 || slash == key.Length - 1)
        {
            return false;
        }

        string hive = key.Substring(0, slash).ToUpperInvariant();
        subKey = key.Substring(slash + 1);
        switch (hive)
        {
            case "HKCU":
            case "HKEY_CURRENT_USER":
                root = Registry.CurrentUser;
                return true;
            case "HKLM":
            case "HKEY_LOCAL_MACHINE":
                root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                return true;
            default:
                return false;
        }
    }

    public static bool KeyExists(string key)
    {
        try
        {
            if (!TrySplitKey(key, out RegistryKey root, out string sub))
            {
                return true; // Unknown hive: treat as existing so it is never deleted.
            }

            using RegistryKey? k = root.OpenSubKey(sub);
            return k != null;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Deletes a key and everything under it. True when the key is gone afterwards.</summary>
    public static bool DeleteKeyTree(string key)
    {
        try
        {
            if (!TrySplitKey(key, out RegistryKey root, out string sub))
            {
                return false;
            }

            root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Deletes a key only if it has no values and no subkeys left.</summary>
    public static void DeleteKeyIfEmpty(string key)
    {
        try
        {
            if (!TrySplitKey(key, out RegistryKey root, out string sub))
            {
                return;
            }

            bool empty;
            using (RegistryKey? k = root.OpenSubKey(sub))
            {
                if (k == null)
                {
                    return;
                }

                empty = k.SubKeyCount == 0 && k.ValueCount == 0;
            }

            if (empty)
            {
                root.DeleteSubKey(sub, throwOnMissingSubKey: false);
            }
        }
        catch
        {
            // In use or access denied; leave it.
        }
    }

    // ------------------------------------------------------------------ services

    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>Names of every service and driver registered on this PC.</summary>
    public static HashSet<string> ServiceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? services = hklm.OpenSubKey(ServicesKey);
            if (services != null)
            {
                names.UnionWith(services.GetSubKeyNames());
            }
        }
        catch
        {
            // Unreadable: nothing is treated as new.
        }

        return names;
    }

    /// <summary>The driver or program file a service runs, as a normal path; null when unknown.</summary>
    public static string? ServiceImagePath(string name)
    {
        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = hklm.OpenSubKey(ServicesKey + "\\" + name);
            if (key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string raw)
            {
                return NormalizeImagePath(raw);
            }
        }
        catch
        {
            // Unreadable.
        }

        return null;
    }

    internal static string? NormalizeImagePath(string raw)
    {
        string p = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (p.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            p = p.Substring(4);
        }
        else if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            p = Path.Combine(windows, p.Substring(12));
        }
        else if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
        {
            p = Path.Combine(windows, p);
        }

        return Path.IsPathRooted(p) ? p : null;
    }

    /// <summary>Stops and deletes a service. True when it no longer exists (or is marked for deletion).</summary>
    public static bool RemoveService(string name)
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            IntPtr svc = OpenService(scm, name, SERVICE_STOP | SERVICE_QUERY_STATUS | DELETE);
            if (svc == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error() == ERROR_SERVICE_DOES_NOT_EXIST;
            }

            try
            {
                var status = new ServiceStatus();
                ControlService(svc, SERVICE_CONTROL_STOP, ref status);
                for (int i = 0; i < 20 && QueryServiceStatus(svc, ref status) && status.CurrentState != SERVICE_STOPPED; i++)
                {
                    Thread.Sleep(100);
                }

                return DeleteService(svc) || Marshal.GetLastWin32Error() == ERROR_SERVICE_MARKED_FOR_DELETE;
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    // ------------------------------------------------------------------ files

    /// <summary>Deletes a file; when it is in use (a loaded driver), schedules it for deletion at the next restart.</summary>
    public static bool DeleteFileOrScheduleDelete(string path, out bool scheduled)
    {
        scheduled = false;
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            scheduled = MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            return false;
        }
    }

    /// <summary>True for %TEMP%, the Windows temp folder and the kit itself: places DiagKit may delete a tool's leftovers from.</summary>
    public static bool IsDisposableLocation(string path, string kitDir)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        IEnumerable<string> roots = new[]
        {
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
            kitDir,
        };
        return roots.Any(r => IsUnder(full, r));
    }

    public static bool IsUnder(string path, string dir)
    {
        if (string.IsNullOrEmpty(dir))
        {
            return false;
        }

        string d = dir.TrimEnd('\\') + "\\";
        return path.StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ pending traces

    /// <summary>
    /// Cleans the traces the ledger lists for this PC (all of them when <paramref name="includePawnIo"/> is set,
    /// otherwise everything except the PawnIO driver) and drops the entries that are gone.
    /// Returns the traces that could not be removed.
    /// </summary>
    public static List<PcTrace> CleanPending(string kitDir, bool includePawnIo, Action<string>? log = null)
    {
        var failed = new List<PcTrace>();
        var done = new List<PcTrace>();
        KitLedger ledger = KitLedger.Load(kitDir);
        foreach (PcTrace t in ledger.TracesOnThisPc().ToList())
        {
            bool ok;
            switch (t.Kind)
            {
                case TraceKinds.RegistryKey:
                    ok = DeleteKeyTree(t.Value);
                    log?.Invoke((ok ? "Removed registry key " : "Could not remove registry key ") + t.Value);
                    break;
                case TraceKinds.Service:
                    ok = RemoveService(t.Value);
                    if (ok && t.Detail.Length > 0 && IsDisposableLocation(t.Detail, kitDir))
                    {
                        ok = DeleteFileOrScheduleDelete(t.Detail, out _);
                    }

                    log?.Invoke((ok ? "Removed driver service " : "Could not remove driver service ") + t.Value);
                    break;
                case TraceKinds.File:
                    ok = DeleteFileOrScheduleDelete(t.Value, out bool scheduled);
                    log?.Invoke(ok ? "Deleted " + t.Value : scheduled ? t.Value + " will be deleted at the next restart" : "Could not delete " + t.Value);
                    break;
                case TraceKinds.PawnIo when includePawnIo:
                    ok = PawnIo.Uninstall(out string message);
                    log?.Invoke(message);
                    break;
                default:
                    continue;
            }

            (ok ? done : failed).Add(t);
        }

        if (done.Count > 0)
        {
            KitLedger.Update(kitDir, l =>
            {
                foreach (PcTrace t in done)
                {
                    l.RemoveTrace(t.Kind, t.Value);
                }
            });
        }

        return failed;
    }

    // ------------------------------------------------------------------ interop

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_STOP = 0x0020;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint DELETE = 0x10000;
    private const uint SERVICE_CONTROL_STOP = 0x00000001;
    private const uint SERVICE_STOPPED = 0x00000001;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;
    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, uint control, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string existing, string? newName, int flags);
}
