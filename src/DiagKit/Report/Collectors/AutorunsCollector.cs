using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiagKit.Models;
using DiagKit.Services;

namespace DiagKit.Report.Collectors;

public sealed class AutorunsCollector : ICollector
{
    public string Name => "Startup entries";
    public string Description => "Unsigned or broken programs that start with Windows";
    public int Phase => 1;
    public TimeSpan Timeout => TimeSpan.FromSeconds(240);

    // Categories that legitimate software rarely uses; non-Microsoft entries here deserve a look.
    private static readonly string[] UnusualCategories = { "Image Hijacks", "Boot Execute", "WMI", "Winlogon", "AppInit", "LSA Providers", "Known DLLs" };

    public async Task CollectAsync(CollectorContext ctx, CollectorStatus status, CancellationToken ct)
    {
        string? exe = ctx.ToolPath("autorunsc");
        if (exe == null)
        {
            status.IsPartial = true;
            status.Detail = "autorunsc64.exe not in the kit (it ships in the Autoruns zip)";
            return;
        }

        ToolDefinition? def = ctx.ToolDefinition("autorunsc");
        RegistryTraceGuard guard = RegistryTraceGuard.Capture(
            def?.RegistryTraces ?? new List<string>(), def?.RegistryParents ?? new List<string>());

        // -a: boot execute, logon, image hijacks, WMI, services/drivers, scheduled tasks, winlogon.
        // -m hides entries signed by Microsoft (with -s), -s verifies signatures, -c outputs CSV.
        string args = "-accepteula -nobanner -a blhmstw -c -s -m";
        status.Detail = "Verifying signatures (this is the slow step)";
        ProcessResult res;
        try
        {
            res = await ProcessRunner.RunAsync(exe, args, Timeout - TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        finally
        {
            guard.Cleanup();
        }

        string csv = TextUtil.Decode(res.StdOut);
        ctx.WriteRaw("autoruns.csv", csv);
        if (res.TimedOut)
        {
            status.IsPartial = true;
            status.Detail = "autorunsc timed out";
            return;
        }

        AutorunSummary? summary = Parse(csv, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (summary == null)
        {
            status.IsPartial = true;
            status.Detail = "Could not read autorunsc output";
            return;
        }

        ctx.Report.Autoruns = summary;
        status.Detail = $"{summary.TotalEntries} non-Microsoft entries, {summary.Unsigned.Count} unsigned, {summary.Missing.Count} pointing at missing files";
    }

    /// <summary>Parses autorunsc CSV. Returns null when the output has no recognisable header.</summary>
    internal static AutorunSummary? Parse(string csv, string windowsDir)
    {
        List<string[]> rows = TextUtil.ParseCsv(csv);
        int headerIndex = rows.FindIndex(r => r.Any(c => c.Equals("Entry Location", StringComparison.OrdinalIgnoreCase)));
        if (headerIndex < 0)
        {
            return null;
        }

        string[] header = rows[headerIndex];
        int Col(string name) => Array.FindIndex(header, h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        int cLocation = Col("Entry Location"), cEntry = Col("Entry"), cEnabled = Col("Enabled"), cCategory = Col("Category"),
            cSigner = Col("Signer"), cCompany = Col("Company"), cImage = Col("Image Path");

        string At(string[] row, int i) => i >= 0 && i < row.Length ? row[i].Trim() : "";

        var summary = new AutorunSummary();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string[] row in rows.Skip(headerIndex + 1))
        {
            string entry = At(row, cEntry);
            if (entry.Length == 0)
            {
                continue;
            }

            string image = At(row, cImage);
            string signer = At(row, cSigner);
            var item = new AutorunEntry
            {
                Category = At(row, cCategory),
                Entry = entry,
                Location = At(row, cLocation),
                ImagePath = image.Length == 0 ? null : image,
                Signer = signer.Length == 0 ? null : signer,
                Company = At(row, cCompany) is { Length: > 0 } company ? company : null,
                Enabled = !At(row, cEnabled).Equals("disabled", StringComparison.OrdinalIgnoreCase),
                Verified = signer.StartsWith("(Verified)", StringComparison.OrdinalIgnoreCase),
                FileMissing = image.StartsWith("File not found", StringComparison.OrdinalIgnoreCase),
            };

            // autorunsc sometimes lists the same entry twice (registry views); count it once.
            if (!seen.Add($"{item.Category}|{item.Location}|{item.Entry}|{item.ImagePath}"))
            {
                continue;
            }

            summary.TotalEntries++;
            if (item.Category.Equals("Logon", StringComparison.OrdinalIgnoreCase) && item.Enabled)
            {
                summary.LogonEntries++;
            }

            if (!item.Enabled)
            {
                continue;
            }

            if (item.FileMissing)
            {
                if (!IsWindowsLeftover(item, windowsDir))
                {
                    summary.Missing.Add(item);
                }
            }
            else if (!item.Verified)
            {
                summary.Unsigned.Add(item);
            }

            if (UnusualCategories.Any(u => item.Category.Equals(u, StringComparison.OrdinalIgnoreCase)))
            {
                summary.UnusualLocations.Add(item);
            }
        }

        return summary;
    }

    /// <summary>
    /// Entries Windows itself leaves pointing at removed files (Adobe Type Manager's atmfd.dll, retired
    /// \Microsoft\Windows\ tasks). They exist on nearly every install and are not uninstall leftovers.
    /// </summary>
    internal static bool IsWindowsLeftover(AutorunEntry item, string windowsDir)
    {
        if (item.Entry.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) ||
            (item.Location?.EndsWith(@"\Font Drivers", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return true;
        }

        string path = (item.ImagePath ?? "").Replace("File not found:", "", StringComparison.OrdinalIgnoreCase).Trim();
        return windowsDir.Length > 0 && path.StartsWith(windowsDir.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase);
    }
}
