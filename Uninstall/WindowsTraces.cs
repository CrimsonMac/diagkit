using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DiagKit.Uninstall;

/// <summary>
/// Windows keeps its own records of programs that ran: Explorer's MuiCache and UserAssist, the Program
/// Compatibility Assistant, background activity (BAM), Prefetch files and Recent items. This removes the entries
/// that point inside the kit folder (under its current or any former name, on any drive letter), so they cover
/// DiagKit and the tools it installed and nothing else: the same tools run from anywhere else are not matched.
/// Windows' system databases (Amcache, ShimCache, SRUM, event logs) are not edited; changing them would risk
/// damaging Windows.
/// </summary>
public sealed class WindowsTraces
{
    private readonly List<Regex> _patterns = new();
    private readonly List<string> _prefetchNeedles = new();
    private readonly HashSet<string> _exeNames;
    private readonly Action<string> _log;

    /// <param name="kitFolders">The kit's path relative to its drive root, e.g. "diagkit.1.1.0" or "Users\me\Desktop\diagkit.1.1.0".</param>
    /// <param name="exeNames">File names of every program in the kit, for Prefetch.</param>
    public WindowsTraces(IEnumerable<string> kitFolders, IEnumerable<string> exeNames, Action<string> log)
    {
        _log = log;
        _exeNames = new HashSet<string>(exeNames.Select(PrefetchName), StringComparer.OrdinalIgnoreCase);
        foreach (string rel in kitFolders.Select(f => f.Trim('\\')).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string r = Regex.Escape(rel);
            // C:\kit\..., \Device\HarddiskVolume3\kit\..., \VOLUME{guid}\kit\... and \\?\ forms.
            _patterns.Add(new Regex(@"^(\\\\\?\\)?[A-Za-z]:\\" + r + @"(\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            _patterns.Add(new Regex(@"^\\Device\\HarddiskVolume\d+\\" + r + @"(\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            _prefetchNeedles.Add("\\" + rel.ToUpperInvariant() + "\\");
        }
    }

    public bool Matches(string pathLike) => _patterns.Any(p => p.IsMatch(pathLike));

    /// <summary>Runs every scrubber; returns how many records were removed.</summary>
    public int ScrubAll()
    {
        int n = 0;
        n += ScrubValueNames(Registry.CurrentUser, @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", "Explorer's program name cache");
        n += ScrubValueNames(Registry.CurrentUser, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store", "Program Compatibility Assistant records");
        n += ScrubValueNames(Registry.CurrentUser, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", "compatibility settings");
        n += ScrubValueNames(LocalMachine(), @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", "compatibility settings (all users)");
        foreach (string sub in new[] { "AppSwitched", "ShowJumpView", "AppBadgeUpdated", "AppLaunch", "TrayButtonClicked" })
        {
            n += ScrubValueNames(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\" + sub, "taskbar usage records");
        }

        n += ScrubValueNames(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search\JumplistData", "Start menu jump list records");
        n += ScrubUserAssist();
        n += ScrubBackgroundActivity();
        n += ScrubRecentItems();
        n += ScrubPrefetch();
        return n;
    }

    private static RegistryKey LocalMachine() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    private int ScrubValueNames(RegistryKey root, string path, string what, Func<string, string>? decode = null)
    {
        int removed = 0;
        try
        {
            using RegistryKey? key = root.OpenSubKey(path, writable: true);
            if (key == null)
            {
                return 0;
            }

            foreach (string name in key.GetValueNames())
            {
                string plain = decode != null ? decode(name) : name;
                if (!Matches(plain))
                {
                    continue;
                }

                try
                {
                    key.DeleteValue(name, false);
                    removed++;
                }
                catch
                {
                    // Protected value; leave it.
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _log($"Could not read {what}: {ex.Message}");
            return 0;
        }

        if (removed > 0)
        {
            _log($"Removed {removed} {(removed == 1 ? "entry" : "entries")} from {what}");
        }

        return removed;
    }

    /// <summary>UserAssist stores ROT13-encoded paths, with known folders written as their GUID.</summary>
    private int ScrubUserAssist()
    {
        int removed = 0;
        try
        {
            using RegistryKey? ua = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist");
            if (ua == null)
            {
                return 0;
            }

            foreach (string guid in ua.GetSubKeyNames())
            {
                removed += ScrubValueNames(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\" + guid + @"\Count",
                    "Explorer's launch history (UserAssist)", name => ExpandKnownFolder(Rot13(name)));
            }
        }
        catch
        {
            // Unreadable; nothing to do.
        }

        return removed;
    }

    /// <summary>Background Activity Moderator: last run time per program, under the user's SID.</summary>
    private int ScrubBackgroundActivity()
    {
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid == null)
        {
            return 0;
        }

        int removed = 0;
        foreach (string service in new[] { "bam", "dam" })
        {
            foreach (string state in new[] { @"State\UserSettings\", @"UserSettings\" })
            {
                removed += ScrubValueNames(LocalMachine(), @"SYSTEM\CurrentControlSet\Services\" + service + @"\" + state + sid, "background activity records");
            }
        }

        return removed;
    }

    /// <summary>Shortcuts in Recent Items (and Office/other recent lists in subfolders) that point into the kit.</summary>
    private int ScrubRecentItems()
    {
        string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(recent))
        {
            return 0;
        }

        int removed = 0;
        foreach (string lnk in SafeFiles(recent, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            string? target = ShellLink.TargetOf(lnk);
            if (target != null && Matches(target))
            {
                try
                {
                    File.Delete(lnk);
                    removed++;
                }
                catch
                {
                    // In use; leave it.
                }
            }
        }

        if (removed > 0)
        {
            _log($"Removed {removed} Recent Items {(removed == 1 ? "shortcut" : "shortcuts")}");
        }

        return removed;
    }

    /// <summary>
    /// Prefetch files are named after the program and store the full path of everything it loaded. Only files
    /// named after a program in the kit are opened, and only those that name the kit folder are deleted.
    /// </summary>
    public int ScrubPrefetch()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        int removed = 0;
        foreach (string pf in SafeFiles(dir, "*.pf", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(pf);
            int dash = name.LastIndexOf('-');
            if (dash <= 0 || !_exeNames.Contains(name.Substring(0, dash)))
            {
                continue;
            }

            try
            {
                string text = Prefetch.ReadStrings(File.ReadAllBytes(pf));
                if (_prefetchNeedles.Any(n => text.IndexOf(n, StringComparison.Ordinal) >= 0))
                {
                    File.Delete(pf);
                    removed++;
                }
            }
            catch
            {
                // Unreadable or in use; leave it.
            }
        }

        if (removed > 0)
        {
            _log($"Removed {removed} Prefetch {(removed == 1 ? "file" : "files")}");
        }

        return removed;
    }

    /// <summary>Prefetch file names use the upper-case program name, cut to 29 characters.</summary>
    internal static string PrefetchName(string exe)
    {
        string upper = exe.ToUpperInvariant();
        return upper.Length > 29 ? upper.Substring(0, 29) : upper;
    }

    internal static string Rot13(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
                >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
                _ => c,
            });
        }

        return sb.ToString();
    }

    /// <summary>"{B4BFCC3A-...}\kit\DiagKit.exe" to "C:\Users\me\Desktop\kit\DiagKit.exe".</summary>
    internal static string ExpandKnownFolder(string path)
    {
        if (path.Length < 39 || path[0] != '{' || path[37] != '}')
        {
            return path;
        }

        if (!Guid.TryParse(path.Substring(0, 38), out Guid id))
        {
            return path;
        }

        try
        {
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out IntPtr ptr) == 0)
            {
                string folder = Marshal.PtrToStringUni(ptr) ?? "";
                Marshal.FreeCoTaskMem(ptr);
                return folder + path.Substring(38);
            }
        }
        catch
        {
            // Unknown folder id.
        }

        return path;
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern, SearchOption option)
    {
        try
        {
            return Directory.GetFiles(dir, pattern, option);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
