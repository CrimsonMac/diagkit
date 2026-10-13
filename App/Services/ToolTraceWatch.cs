using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiagKit.Kit;
using DiagKit.Models;

namespace DiagKit.Services;

/// <summary>
/// Removes the driver services and temp files a tool leaves on the PC after it closes. Only things that did not
/// exist before the launch and that match the tool's "services"/"files" patterns in tools.json are touched, so a
/// driver the PC owner's own copy of the tool installed is left alone. Anything that cannot be removed yet (a
/// driver still loaded) is recorded in the kit ledger and retried at the next start or by Uninstall.
/// </summary>
public sealed class ToolTraceWatch
{
    private sealed class Session
    {
        public int Running;
        public DateTime Started;
        public HashSet<string> ServicesBefore = new(StringComparer.OrdinalIgnoreCase);
    }

    // One session per tool id, so a second copy of a tool closing does not pull the driver from under the first.
    private static readonly Dictionary<string, Session> Sessions = new(StringComparer.OrdinalIgnoreCase);

    private readonly ToolDefinition _tool;
    private int _ended;

    private ToolTraceWatch(ToolDefinition tool)
    {
        _tool = tool;
    }

    public static ToolTraceWatch Begin(ToolDefinition tool)
    {
        var watch = new ToolTraceWatch(tool);
        if (!HasPatterns(tool))
        {
            return watch;
        }

        lock (Sessions)
        {
            if (!Sessions.TryGetValue(tool.Id, out Session? s))
            {
                s = new Session
                {
                    // File times on FAT32 sticks have 2-second resolution.
                    Started = DateTime.Now.AddSeconds(-2),
                    ServicesBefore = tool.Services.Count > 0 ? TraceCleaner.ServiceNames() : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                };
                Sessions[tool.Id] = s;
            }

            s.Running++;
        }

        return watch;
    }

    private static bool HasPatterns(ToolDefinition tool) => tool.Services.Count > 0 || tool.Files.Count > 0;

    public bool HasWork => HasPatterns(_tool);

    /// <summary>Called once the tool has exited.</summary>
    /// <param name="clean">False when the launch failed or was handed to an already running copy: just drop the hold.</param>
    public void End(bool clean = true)
    {
        if (!HasPatterns(_tool) || System.Threading.Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }

        Session? session;
        lock (Sessions)
        {
            if (!Sessions.TryGetValue(_tool.Id, out session) || --session.Running > 0)
            {
                return;
            }

            Sessions.Remove(_tool.Id);
        }

        if (!clean)
        {
            return;
        }

        var pending = new List<(string Kind, string Value, string Detail)>();
        string kit = AppPaths.BaseDir;

        if (_tool.Services.Count > 0)
        {
            foreach (string name in TraceCleaner.ServiceNames().Where(n => !session.ServicesBefore.Contains(n)))
            {
                string? image = TraceCleaner.ServiceImagePath(name);
                bool ours = _tool.Services.Any(p => Wildcard.IsMatch(p, name)) ||
                            (image != null && TraceCleaner.IsUnder(image, kit));
                if (!ours)
                {
                    continue;
                }

                // The driver file goes too when it sits somewhere disposable, or was written to System32\drivers by this run.
                bool deleteFile = image != null && (TraceCleaner.IsDisposableLocation(image, kit) || CreatedSince(image, session.Started));
                bool removed = TraceCleaner.RemoveService(name);
                if (removed && deleteFile && image != null && !TraceCleaner.DeleteFileOrScheduleDelete(image, out _))
                {
                    pending.Add((TraceKinds.File, image, _tool.Name));
                }
                else if (!removed)
                {
                    pending.Add((TraceKinds.Service, name, deleteFile ? image ?? "" : ""));
                }
            }
        }

        foreach (string pattern in _tool.Files)
        {
            foreach (string file in Expand(pattern).Where(f => CreatedSince(f, session.Started)))
            {
                if (!TraceCleaner.DeleteFileOrScheduleDelete(file, out _))
                {
                    pending.Add((TraceKinds.File, file, _tool.Name));
                }
            }
        }

        if (pending.Count > 0)
        {
            KitLedger.Update(kit, l =>
            {
                foreach ((string kind, string value, string detail) in pending)
                {
                    l.AddTrace(kind, value, detail);
                }
            });
        }
    }

    private static bool CreatedSince(string path, DateTime since)
    {
        try
        {
            return File.Exists(path) && File.GetCreationTime(path) >= since;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>"%TEMP%\cpuz*.sys" to the matching files. Wildcards are only allowed in the file name.</summary>
    private static IEnumerable<string> Expand(string pattern)
    {
        string path = Environment.ExpandEnvironmentVariables(pattern);
        string? dir = Path.GetDirectoryName(path);
        string name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(dir) || !Path.IsPathRooted(dir) || !Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.GetFiles(dir, name);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
