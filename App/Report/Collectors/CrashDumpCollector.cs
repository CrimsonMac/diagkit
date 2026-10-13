using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Win32;

namespace DiagKit.Report.Collectors;

public sealed class CrashDumpCollector : ICollector
{
    public string Name => "Crash dumps";
    public string Description => "Which driver each blue screen points at";
    public int Phase => 1;
    public TimeSpan Timeout => TimeSpan.FromSeconds(90);

    public async Task CollectAsync(CollectorContext ctx, CollectorStatus status, CancellationToken ct)
    {
        string dumpDir = MinidumpFolder();
        List<FileInfo> dumps = new();
        try
        {
            if (Directory.Exists(dumpDir))
            {
                dumps = new DirectoryInfo(dumpDir).GetFiles("*.dmp").OrderByDescending(f => f.LastWriteTime).ToList();
            }
        }
        catch (UnauthorizedAccessException)
        {
            status.IsPartial = true;
        }

        if (dumps.Count == 0)
        {
            ctx.Report.CrashDumpsAnalyzed = true;
            status.Detail = "No crash dumps on this PC";
            return;
        }

        string? bsv = ctx.ToolPath("bluescreenview");
        if (bsv == null)
        {
            foreach (FileInfo f in dumps)
            {
                ctx.Report.CrashDumps.Add(new CrashDump { File = f.Name, Time = f.LastWriteTime });
            }

            status.IsPartial = true;
            status.Detail = $"{TextUtil.Plural(dumps.Count, "dump")} found. BlueScreenView not in the kit, so drivers were not identified";
            return;
        }

        string xmlPath = ctx.RawPath("bluescreenview.xml");
        status.Detail = $"Analysing {TextUtil.Plural(dumps.Count, "dump")}";
        // Name the folder explicitly: BlueScreenView otherwise uses whatever its .cfg remembers from the last GUI session.
        string args = $"/LoadFrom 1 /MiniDumpFolder \"{dumpDir.TrimEnd('\\')}\" /sxml \"{xmlPath}\"";
        ProcessResult res = await ProcessRunner.RunAsync(bsv, args, TimeSpan.FromSeconds(75), ct).ConfigureAwait(false);
        if (res.TimedOut || !File.Exists(xmlPath))
        {
            status.IsPartial = true;
            status.Detail = "BlueScreenView did not produce a report";
            return;
        }

        List<CrashDump> parsed = Parse(xmlPath, dumps);
        ctx.Report.CrashDumps.AddRange(parsed.OrderByDescending(c => c.Time));
        ctx.Report.CrashDumpsAnalyzed = true;

        var drivers = parsed.Where(c => !string.IsNullOrEmpty(c.CausedByDriver))
            .GroupBy(c => c.CausedByDriver!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(2)
            .ToList();
        status.Detail = TextUtil.Plural(parsed.Count, "dump") + " analysed" +
                        (drivers.Count > 0 ? ", most point at " + string.Join(" and ", drivers) : "");
    }

    /// <summary>Where Windows writes minidumps: CrashControl\MinidumpDir (admins can move it), else %SystemRoot%\Minidump.</summary>
    private static string MinidumpFolder()
    {
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CrashControl");
            // REG_EXPAND_SZ: GetValue expands %SystemRoot% by default.
            return key?.GetValue("MinidumpDir") is string dir && !string.IsNullOrWhiteSpace(dir) ? dir.Trim() : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static List<CrashDump> Parse(string xmlPath, List<FileInfo> dumps)
    {
        var result = new List<CrashDump>();
        byte[] bytes = File.ReadAllBytes(xmlPath);
        string text = DecodeXml(bytes);

        // Parsing from a string ignores the declared encoding, which NirSoft does not always get right.
        XDocument doc = XDocument.Parse(text);
        foreach (XElement item in doc.Descendants().Where(e => e.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase)))
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement e in item.Elements())
            {
                fields[Key(e.Name.LocalName)] = e.Value.Trim();
            }

            string file = Field(fields, "dumpfile") ?? "";
            if (file.Length == 0)
            {
                continue;
            }

            FileInfo? match = dumps.FirstOrDefault(d => d.Name.Equals(file, StringComparison.OrdinalIgnoreCase));
            DateTime? time = match?.LastWriteTime;
            if (time == null && DateTime.TryParse(Field(fields, "crashtime"), CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out DateTime parsedTime))
            {
                time = parsedTime;
            }

            string? code = Field(fields, "bugcheckcode");
            result.Add(new CrashDump
            {
                File = file,
                Time = time,
                BugCheckCode = code == null ? null : BugChecks.Normalize(code),
                BugCheckString = Field(fields, "bugcheckstring"),
                CausedByDriver = Field(fields, "causedbydriver"),
                DriverDescription = Field(fields, "filedescription"),
                DriverCompany = Field(fields, "company"),
            });
        }

        return result;
    }

    private static string DecodeXml(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Compat.Latin1.GetString(bytes);
        }
    }

    private static string Key(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    private static string? Field(Dictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}
