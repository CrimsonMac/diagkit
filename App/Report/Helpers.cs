using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DiagKit.Report;

/// <summary>Thin WMI wrapper that copies results into dictionaries so COM objects are released immediately.</summary>
internal static class Wmi
{
    public static List<Dictionary<string, object?>> Query(string wql, string scope = @"root\cimv2", int timeoutSeconds = 20)
    {
        var rows = new List<Dictionary<string, object?>>();
        var options = new System.Management.EnumerationOptions
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            ReturnImmediately = true,
            Rewindable = false,
        };

        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), options);
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementBaseObject obj in results)
        {
            using (obj)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (PropertyData p in obj.Properties)
                {
                    row[p.Name] = p.Value;
                }

                rows.Add(row);
            }
        }

        return rows;
    }

    public static string? Str(this Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out object? v) || v == null)
        {
            return null;
        }

        string? s = v is char c ? (c == '\0' ? null : c.ToString()) : Convert.ToString(v, CultureInfo.InvariantCulture);
        s = s?.Trim().TrimEnd('\0');
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static long? Long(this Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out object? v) || v == null)
        {
            return null;
        }

        try
        {
            return v switch
            {
                ulong u => u > long.MaxValue ? long.MaxValue : (long)u,
                string s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : null,
                _ => Convert.ToInt64(v, CultureInfo.InvariantCulture),
            };
        }
        catch
        {
            return null;
        }
    }

    public static int? Int(this Dictionary<string, object?> row, string key)
    {
        long? l = row.Long(key);
        return l is null ? null : (int)Compat.Clamp(l.Value, int.MinValue, int.MaxValue);
    }

    public static bool? Bool(this Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out object? v) && v is bool b ? b : null;

    public static DateTime? Dmtf(this Dictionary<string, object?> row, string key)
    {
        string? s = row.Str(key);
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        try
        {
            return ManagementDateTimeConverter.ToDateTime(s);
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record ProcessResult(int ExitCode, byte[] StdOut, string StdErr, bool TimedOut);

internal static class ProcessRunner
{
    /// <summary>Runs a console tool hidden, capturing stdout as raw bytes (encoding varies between tools).</summary>
    public static async Task<ProcessResult> RunAsync(string exe, string args, TimeSpan timeout, CancellationToken ct, string? workDir = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir ?? Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();
        process.StandardInput.Close();

        Task<byte[]> stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                Compat.KillTree(process);
            }
            catch
            {
                // Already gone.
            }

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }

        byte[] stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        int code = timedOut ? -1 : SafeExitCode(process);
        return new ProcessResult(code, stdout, stderr, timedOut);
    }

    private static int SafeExitCode(Process p)
    {
        try
        {
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream s)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms).ConfigureAwait(false);
        return ms.ToArray();
    }
}

internal static class TextUtil
{
    /// <summary>Decodes tool output that may be UTF-16LE (Sysinternals), UTF-8, or UTF-8 with BOM.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        int n = Math.Min(bytes.Length, 4096);
        int zeros = 0;
        for (int i = 1; i < n; i += 2)
        {
            if (bytes[i] == 0)
            {
                zeros++;
            }
        }

        if (n > 16 && zeros > n / 4)
        {
            return Encoding.Unicode.GetString(bytes, 0, bytes.Length - (bytes.Length % 2));
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>RFC 4180 CSV parser (quoted fields, doubled quotes, embedded newlines).</summary>
    public static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    if (row.Count > 1 || row[0].Length > 0)
                    {
                        rows.Add(row.ToArray());
                    }

                    row.Clear();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }

        return rows;
    }

    public static string Bytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }

    /// <summary>Drive sizes the way manufacturers label them (decimal), e.g. "1 TB", "500 GB".</summary>
    public static string DecimalSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "";
        }

        double gb = bytes / 1_000_000_000.0;
        return gb >= 1000 ? $"{gb / 1000:0.#} TB" : $"{gb:0} GB";
    }

    public static string Plural(int n, string singular, string? plural = null)
        => n == 1 ? $"1 {singular}" : $"{n} {plural ?? singular + "s"}";

    public static string Ago(DateTime? when)
    {
        if (when == null)
        {
            return "";
        }

        TimeSpan d = DateTime.Now - when.Value;
        if (d.TotalMinutes < 2)
        {
            return "just now";
        }

        if (d.TotalHours < 2)
        {
            return $"{(int)d.TotalMinutes} minutes ago";
        }

        if (d.TotalDays < 2)
        {
            return $"{(int)d.TotalHours} hours ago";
        }

        return $"{(int)d.TotalDays} days ago";
    }

    public static string Date(DateTime? when) => when?.ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? "unknown";

    public static string DateTimeShort(DateTime? when) => when?.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture) ?? "unknown";
}
