using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32;

namespace DiagKit.Services;

/// <summary>
/// Snapshots which of a tool's registry keys already exist before launch and, after the tool exits,
/// deletes only the ones it created. Existing settings of the PC owner are never touched.
/// Keys are reference-counted across guards, so overlapping launches that share a key (two copies of
/// a tool, or Autoruns and Process Explorer under HKCU\Software\Sysinternals) clean up once, after the last one exits.
/// </summary>
public sealed class RegistryTraceGuard
{
    // Keys DiagKit's tools created and that are still in use, with the number of guards holding each.
    private static readonly Dictionary<string, int> Owned = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _createdKeys = new();
    private readonly List<string> _createdParents = new();
    private int _cleaned;

    private RegistryTraceGuard()
    {
    }

    public static RegistryTraceGuard Capture(IEnumerable<string> traces, IEnumerable<string> parents)
    {
        var guard = new RegistryTraceGuard();
        lock (Owned)
        {
            Claim(traces, guard._createdKeys);
            Claim(parents, guard._createdParents);
        }

        return guard;
    }

    private static void Claim(IEnumerable<string> keys, List<string> claimed)
    {
        foreach (string key in keys)
        {
            if (Owned.TryGetValue(key, out int holders))
            {
                Owned[key] = holders + 1;
                claimed.Add(key);
            }
            else if (!KeyExists(key))
            {
                Owned[key] = 1;
                claimed.Add(key);
            }
        }
    }

    /// <summary>Drops this guard's hold on a key; true when no other running tool still holds it.</summary>
    private static bool Release(string key)
    {
        if (!Owned.TryGetValue(key, out int holders))
        {
            return false;
        }

        if (holders > 1)
        {
            Owned[key] = holders - 1;
            return false;
        }

        Owned.Remove(key);
        return true;
    }

    public bool HasWork => _createdKeys.Count > 0 || _createdParents.Count > 0;

    public void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) != 0)
        {
            return;
        }

        lock (Owned)
        {
            CleanupLocked();
        }
    }

    private void CleanupLocked()
    {
        foreach (string key in _createdKeys)
        {
            if (!Release(key))
            {
                continue;
            }

            try
            {
                if (Split(key) is { } p)
                {
                    p.Root.DeleteSubKeyTree(p.SubKey, throwOnMissingSubKey: false);
                }
            }
            catch
            {
                // Key in use or access denied; leave it.
            }
        }

        foreach (string key in _createdParents)
        {
            if (!Release(key))
            {
                continue;
            }

            try
            {
                if (Split(key) is not { } p)
                {
                    continue;
                }

                bool empty;
                using (RegistryKey? k = p.Root.OpenSubKey(p.SubKey))
                {
                    if (k == null)
                    {
                        continue;
                    }

                    empty = k.SubKeyCount == 0 && k.ValueCount == 0;
                }

                if (empty)
                {
                    p.Root.DeleteSubKey(p.SubKey, throwOnMissingSubKey: false);
                }
            }
            catch
            {
                // Ignore.
            }
        }
    }

    private static bool KeyExists(string key)
    {
        try
        {
            if (Split(key) is not { } p)
            {
                return true; // Unknown hive: treat as existing so it is never deleted.
            }

            using RegistryKey? k = p.Root.OpenSubKey(p.SubKey);
            return k != null;
        }
        catch
        {
            return true;
        }
    }

    private readonly record struct KeyPath(RegistryKey Root, string SubKey);

    private static KeyPath? Split(string key)
    {
        int slash = key.IndexOf('\\');
        if (slash <= 0 || slash == key.Length - 1)
        {
            return null;
        }

        string hive = key.Substring(0, slash).ToUpperInvariant();
        string sub = key.Substring(slash + 1);
        RegistryKey? root = hive switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            _ => null,
        };

        return root == null ? null : new KeyPath(root, sub);
    }
}
