using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DiagKit.Kit;
using Microsoft.Win32;

namespace DiagKit.Services;

/// <summary>
/// Leaves the registry as it was before a tool ran:
/// - keys the tool creates are deleted after it exits;
/// - keys that already existed (the PC owner's own copy of the tool, for example) are snapshotted before launch
///   and restored afterwards, so their settings are never changed.
/// Created keys are also written to the kit ledger until they are deleted, so a key left behind because DiagKit
/// closed first is still cleaned up later (at the next start, or by Uninstall).
/// Keys are reference-counted across guards, so overlapping launches that share a key (two copies of a tool,
/// or Autoruns and Process Explorer under HKCU\Software\Sysinternals) clean up once, after the last one exits.
/// </summary>
public sealed class RegistryTraceGuard
{
    private sealed class Hold
    {
        public int Holders;

        /// <summary>Null when DiagKit's tool created the key; otherwise the state to put back.</summary>
        public KeySnapshot? Snapshot;
    }

    // Keys currently held by running tools.
    private static readonly Dictionary<string, Hold> Held = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> _keys = new();
    private readonly List<string> _createdParents = new();
    private int _cleaned;

    private RegistryTraceGuard()
    {
    }

    public static RegistryTraceGuard Capture(IEnumerable<string> traces, IEnumerable<string> parents)
    {
        var guard = new RegistryTraceGuard();
        var created = new List<string>();
        lock (Held)
        {
            foreach (string key in traces)
            {
                if (Held.TryGetValue(key, out Hold? hold))
                {
                    hold.Holders++;
                }
                else
                {
                    bool exists = TraceCleaner.KeyExists(key);
                    Held[key] = new Hold { Holders = 1, Snapshot = exists ? KeySnapshot.Take(key) : null };
                    if (!exists)
                    {
                        created.Add(key);
                    }
                }

                guard._keys.Add(key);
            }

            foreach (string key in parents)
            {
                // Parents are only ever removed when DiagKit's tools created them and they ended up empty.
                if (Held.TryGetValue(key, out Hold? hold))
                {
                    hold.Holders++;
                    guard._createdParents.Add(key);
                }
                else if (!TraceCleaner.KeyExists(key))
                {
                    Held[key] = new Hold { Holders = 1 };
                    guard._createdParents.Add(key);
                    created.Add(key);
                }
            }
        }

        if (created.Count > 0)
        {
            KitLedger.Update(AppPaths.BaseDir, l =>
            {
                foreach (string key in created)
                {
                    l.AddTrace(TraceKinds.RegistryKey, key);
                }
            });
        }

        return guard;
    }

    /// <summary>Drops this guard's hold on a key; returns the hold when no other running tool still has it.</summary>
    private static Hold? Release(string key)
    {
        if (!Held.TryGetValue(key, out Hold? hold))
        {
            return null;
        }

        if (--hold.Holders > 0)
        {
            return null;
        }

        Held.Remove(key);
        return hold;
    }

    public bool HasWork => _keys.Count > 0 || _createdParents.Count > 0;

    /// <summary>
    /// Drops this guard's holds without cleaning anything, for launches handed to a copy that was already running.
    /// Keys DiagKit's tools created stay listed in the ledger and are removed at the next start or by Uninstall.
    /// </summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) != 0)
        {
            return;
        }

        lock (Held)
        {
            foreach (string key in _keys.Concat(_createdParents))
            {
                Release(key);
            }
        }
    }

    public void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) != 0)
        {
            return;
        }

        var removed = new List<string>();
        lock (Held)
        {
            foreach (string key in _keys)
            {
                Hold? hold = Release(key);
                if (hold == null)
                {
                    continue;
                }

                if (hold.Snapshot != null)
                {
                    hold.Snapshot.Restore();
                }
                else if (TraceCleaner.DeleteKeyTree(key))
                {
                    removed.Add(key);
                }
            }

            foreach (string key in _createdParents)
            {
                if (Release(key) != null)
                {
                    TraceCleaner.DeleteKeyIfEmpty(key);
                    if (!TraceCleaner.KeyExists(key))
                    {
                        removed.Add(key);
                    }
                }
            }
        }

        if (removed.Count > 0)
        {
            KitLedger.Update(AppPaths.BaseDir, l =>
            {
                foreach (string key in removed)
                {
                    l.RemoveTrace(TraceKinds.RegistryKey, key);
                }
            });
        }
    }
}

/// <summary>A registry key with all its values and subkeys, to put back exactly as it was.</summary>
public sealed class KeySnapshot
{
    private readonly string _path;
    private readonly List<(string Name, object Value, RegistryValueKind Kind)> _values = new();
    private readonly List<KeySnapshot> _children = new();
    private readonly string _name;

    private KeySnapshot(string path, string name)
    {
        _path = path;
        _name = name;
    }

    public static KeySnapshot? Take(string key)
    {
        try
        {
            if (!TraceCleaner.TrySplitKey(key, out RegistryKey root, out string sub))
            {
                return null;
            }

            using RegistryKey? k = root.OpenSubKey(sub);
            if (k == null)
            {
                return null;
            }

            var snap = new KeySnapshot(key, "");
            snap.Read(k, 0);
            return snap;
        }
        catch
        {
            return null;
        }
    }

    private void Read(RegistryKey key, int depth)
    {
        foreach (string name in key.GetValueNames())
        {
            object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value != null)
            {
                _values.Add((name, value, key.GetValueKind(name)));
            }
        }

        // Tool settings are shallow; the limit only guards against pathological trees.
        if (depth >= 8)
        {
            return;
        }

        foreach (string sub in key.GetSubKeyNames())
        {
            using RegistryKey? child = key.OpenSubKey(sub);
            if (child != null)
            {
                var snap = new KeySnapshot(_path + "\\" + sub, sub);
                snap.Read(child, depth + 1);
                _children.Add(snap);
            }
        }
    }

    public void Restore()
    {
        try
        {
            if (!TraceCleaner.TrySplitKey(_path, out RegistryKey root, out string sub))
            {
                return;
            }

            using RegistryKey key = root.CreateSubKey(sub);
            Write(key);
        }
        catch
        {
            // Access denied or key in use: leave it as the tool left it.
        }
    }

    private void Write(RegistryKey key)
    {
        var names = new HashSet<string>(_values.Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        foreach (string name in key.GetValueNames().Where(n => !names.Contains(n)))
        {
            key.DeleteValue(name, false);
        }

        foreach ((string name, object value, RegistryValueKind kind) in _values)
        {
            try
            {
                key.SetValue(name, value, kind);
            }
            catch
            {
                // A value type .NET cannot write back (REG_NONE and similar); keep restoring the rest.
            }
        }

        var children = new HashSet<string>(_children.Select(c => c._name), StringComparer.OrdinalIgnoreCase);
        foreach (string extra in key.GetSubKeyNames().Where(n => !children.Contains(n)))
        {
            key.DeleteSubKeyTree(extra, false);
        }

        foreach (KeySnapshot child in _children)
        {
            using RegistryKey sub = key.CreateSubKey(child._name);
            child.Write(sub);
        }
    }
}
