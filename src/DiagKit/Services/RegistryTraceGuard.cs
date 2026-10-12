using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace DiagKit.Services;

/// <summary>
/// Snapshots which of a tool's registry keys already exist before launch and, after the tool exits,
/// deletes only the ones it created. Existing settings of the PC owner are never touched.
/// </summary>
public sealed class RegistryTraceGuard
{
    private readonly List<string> _createdKeys = new();
    private readonly List<string> _createdParents = new();

    private RegistryTraceGuard()
    {
    }

    public static RegistryTraceGuard Capture(IEnumerable<string> traces, IEnumerable<string> parents)
    {
        var guard = new RegistryTraceGuard();
        foreach (string key in traces)
        {
            if (!KeyExists(key))
            {
                guard._createdKeys.Add(key);
            }
        }

        foreach (string key in parents)
        {
            if (!KeyExists(key))
            {
                guard._createdParents.Add(key);
            }
        }

        return guard;
    }

    public bool HasWork => _createdKeys.Count > 0 || _createdParents.Count > 0;

    public void Cleanup()
    {
        foreach (string key in _createdKeys)
        {
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
