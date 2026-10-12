using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DiagKit.Models;

namespace DiagKit.Services;

public sealed class ManifestLoadResult
{
    public required ToolManifest Manifest { get; init; }
    public string? Warning { get; init; }
}

public static class ToolCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string ReadDefaultManifestText()
    {
        Assembly asm = typeof(ToolCatalog).Assembly;
        using Stream? s = asm.GetManifestResourceStream("DiagKit.tools.default.json");
        if (s == null)
        {
            return "{\"version\":1,\"tools\":[]}";
        }

        using var reader = new StreamReader(s);
        return reader.ReadToEnd();
    }

    /// <summary>Reads tools.json next to the exe, creating it from the built-in default when missing.</summary>
    public static ManifestLoadResult LoadManifest()
    {
        string path = AppPaths.ManifestPath;
        string? warning = null;
        string text;

        if (File.Exists(path))
        {
            text = File.ReadAllText(path);
        }
        else
        {
            text = ReadDefaultManifestText();
            try
            {
                File.WriteAllText(path, text);
            }
            catch (Exception ex)
            {
                warning = "Could not write tools.json next to DiagKit (" + ex.Message + "). Using the built-in tool list.";
            }
        }

        ToolManifest? manifest = null;
        try
        {
            manifest = JsonSerializer.Deserialize<ToolManifest>(text, JsonOptions);
        }
        catch (JsonException ex)
        {
            warning = $"tools.json has an error on line {(ex.LineNumber ?? 0) + 1}: {ex.Message} Using the built-in tool list.";
        }

        if (manifest == null || manifest.Tools.Count == 0)
        {
            manifest = JsonSerializer.Deserialize<ToolManifest>(ReadDefaultManifestText(), JsonOptions) ?? new ToolManifest();
        }

        // Drop malformed entries instead of failing the whole list.
        manifest.Tools = manifest.Tools
            .Where(t => !string.IsNullOrWhiteSpace(t.Id) && t.Executables.Count > 0)
            .ToList();

        EnsureToolFolders(manifest);
        return new ManifestLoadResult { Manifest = manifest, Warning = warning };
    }

    /// <summary>Creates tools\&lt;folder&gt; for every entry so it is obvious where each exe goes.</summary>
    private static void EnsureToolFolders(ToolManifest manifest)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ToolsRoot);
            foreach (string folder in manifest.Tools.Select(t => t.Folder).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string dir = Path.Combine(AppPaths.ToolsRoot, folder);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
        }
        catch
        {
            // Read-only media: resolution still works for whatever exists.
        }
    }

    private static readonly EnumerationOptions SearchOptions = new()
    {
        RecurseSubdirectories = true,
        MaxRecursionDepth = 3,
        MatchCasing = MatchCasing.CaseInsensitive,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System,
    };

    /// <summary>
    /// Finds the executable for a tool: first in its own folder, then anywhere under tools\
    /// (so a zip extracted into a differently named folder still works).
    /// </summary>
    public static string? Resolve(ToolDefinition tool)
    {
        string root = AppPaths.ToolsRoot;
        if (!Directory.Exists(root))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(tool.Folder))
        {
            string own = Path.Combine(root, tool.Folder);
            string? hit = FindFirst(own, tool.Executables);
            if (hit != null)
            {
                return hit;
            }
        }

        return FindFirst(root, tool.Executables);
    }

    private static string? FindFirst(string dir, IEnumerable<string> patterns)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        foreach (string pattern in patterns)
        {
            try
            {
                // Shortest path wins: prefer the copy nearest the tool folder root.
                string? best = Directory.EnumerateFiles(dir, pattern, SearchOptions)
                    .OrderBy(p => p.Length)
                    .FirstOrDefault();
                if (best != null)
                {
                    return best;
                }
            }
            catch (Exception)
            {
                // Bad pattern or unreadable folder; try the next pattern.
            }
        }

        return null;
    }
}
