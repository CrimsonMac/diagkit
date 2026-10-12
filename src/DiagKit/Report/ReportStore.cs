using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiagKit.Services;

namespace DiagKit.Report;

public sealed class ReportListing
{
    public string Folder { get; init; } = "";
    public string MachineName { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public int Critical { get; init; }
    public int Warning { get; init; }
    public int Info { get; init; }
    public string? HtmlPath { get; init; }
}

public static class ReportStore
{
    public const string JsonFile = "report.json";
    public const string HtmlFile = "report.html";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string CreateFolder(string machineName, DateTime when)
    {
        var safe = new StringBuilder();
        foreach (char c in machineName)
        {
            safe.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }

        string root = AppPaths.ReportsRoot;
        string baseName = $"{safe}_{when:yyyy-MM-dd_HHmmss}";
        string folder = Path.Combine(root, baseName);
        int n = 2;
        while (Directory.Exists(folder))
        {
            folder = Path.Combine(root, $"{baseName}_{n++}");
        }

        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Writes report.json and report.html. Returns the HTML path.</summary>
    public static string Save(DiagnosticReport report, string folder)
    {
        File.WriteAllText(Path.Combine(folder, JsonFile), JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
        string html = Path.Combine(folder, HtmlFile);
        File.WriteAllText(html, HtmlReportWriter.Render(report), new UTF8Encoding(false));
        return html;
    }

    public static DiagnosticReport? Load(string folder)
    {
        string path = Path.Combine(folder, JsonFile);
        if (!File.Exists(path))
        {
            return null;
        }

        using FileStream fs = File.OpenRead(path);
        return JsonSerializer.Deserialize<DiagnosticReport>(fs, JsonOptions);
    }

    public static List<ReportListing> List()
    {
        var result = new List<ReportListing>();
        string root = AppPaths.ReportsRoot;
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                DiagnosticReport? r = Load(dir);
                if (r == null)
                {
                    continue;
                }

                string html = Path.Combine(dir, HtmlFile);
                result.Add(new ReportListing
                {
                    Folder = dir,
                    MachineName = r.MachineName,
                    CreatedAt = r.CreatedAt,
                    Critical = r.Findings.Count(f => f.Severity == Severity.Critical),
                    Warning = r.Findings.Count(f => f.Severity == Severity.Warning),
                    Info = r.Findings.Count(f => f.Severity == Severity.Info),
                    HtmlPath = File.Exists(html) ? html : null,
                });
            }
            catch (Exception ex)
            {
                AppPaths.LogError("Could not read report in " + dir, ex);
            }
        }

        return result.OrderByDescending(r => r.CreatedAt).ToList();
    }
}
