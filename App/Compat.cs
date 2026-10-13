using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DiagKit;

/// <summary>File name patterns with * and ? (case-insensitive), as used in tools.json.</summary>
public static class Wildcard
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsMatch(string pattern, string name)
    {
        Regex rx = Cache.GetOrAdd(pattern, p => new Regex(
            "^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        return rx.IsMatch(name);
    }
}

/// <summary>Small helpers that newer .NET versions have built in.</summary>
public static class Compat
{
    /// <summary>ISO-8859-1: every byte maps to the same code point.</summary>
    public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

    /// <summary>Full path of DiagKit.exe.</summary>
    public static readonly string ProcessPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";

    public static string GetRelativePath(string relativeTo, string path)
    {
        string from = relativeTo.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (path.StartsWith(from, StringComparison.OrdinalIgnoreCase))
        {
            return path.Substring(from.Length);
        }

        Uri rel = new Uri(from).MakeRelativeUri(new Uri(path));
        return Uri.UnescapeDataString(rel.ToString()).Replace('/', Path.DirectorySeparatorChar);
    }

    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    public static long Clamp(long value, long min, long max) => value < min ? min : value > max ? max : value;

    public static string Replace(this string s, string oldValue, string newValue, StringComparison comparison)
    {
        if (oldValue.Length == 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        int start = 0;
        int hit;
        while ((hit = s.IndexOf(oldValue, start, comparison)) >= 0)
        {
            sb.Append(s, start, hit - start).Append(newValue);
            start = hit + oldValue.Length;
        }

        return sb.Append(s, start, s.Length - start).ToString();
    }

    public static bool Contains(this string s, string value, StringComparison comparison) => s.IndexOf(value, comparison) >= 0;

    /// <summary>Completes with the task, or throws TimeoutException / OperationCanceledException first.</summary>
    public static async Task WaitAsync(this Task task, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task delay = Task.Delay(timeout, cts.Token);
        Task first = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (first == task)
        {
            cts.Cancel();
            await task.ConfigureAwait(false);
            return;
        }

        ct.ThrowIfCancellationRequested();
        throw new TimeoutException();
    }

    /// <summary>Kills a process and every process it started.</summary>
    public static void KillTree(Process process)
    {
        int id;
        try
        {
            id = process.Id;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        KillChildren(id);
        try
        {
            process.Kill();
        }
        catch
        {
            // Already gone.
        }
    }

    private static void KillChildren(int parentId)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId FROM Win32_Process WHERE ParentProcessId=" + parentId);
            foreach (System.Management.ManagementBaseObject o in searcher.Get())
            {
                using (o)
                {
                    int child = Convert.ToInt32(o["ProcessId"]);
                    KillChildren(child);
                    try
                    {
                        using Process p = Process.GetProcessById(child);
                        p.Kill();
                    }
                    catch
                    {
                        // Already gone.
                    }
                }
            }
        }
        catch
        {
            // WMI unavailable: the parent is still killed.
        }
    }

    public static Task WaitForExitAsync(this Process process, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => tcs.TrySetResult(true);
        if (process.HasExited)
        {
            tcs.TrySetResult(true);
        }

        CancellationTokenRegistration reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task.ContinueWith(t =>
        {
            reg.Dispose();
            return t;
        }, TaskScheduler.Default).Unwrap();
    }
}
