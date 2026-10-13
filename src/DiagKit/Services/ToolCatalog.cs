using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
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

    /// <summary>How many folder levels below the folder being searched an executable may sit.</summary>
    private const int SearchDepth = 3;

    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        // One extra level, so a tool's own folder (tools\<folder>) can be searched SearchDepth levels deep too.
        MaxRecursionDepth = SearchDepth + 1,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System,
    };

    /// <summary>
    /// Finds the executable for each tool: first in its own folder, then anywhere under tools\
    /// (so a zip extracted into a differently named folder still works). The folder tree is read once
    /// for all tools; on a slow USB stick the directory walk is the expensive part.
    /// </summary>
    public static List<string?> ResolveAll(IReadOnlyList<ToolDefinition> tools)
    {
        string root = AppPaths.ToolsRoot;
        List<string> files = new();
        try
        {
            if (Directory.Exists(root))
            {
                files = Directory.EnumerateFiles(root, "*", WalkOptions).ToList();
            }
        }
        catch (Exception)
        {
            // Unreadable tools folder: every tool shows as missing.
        }

        var result = new List<string?>(tools.Count);
        foreach (ToolDefinition tool in tools)
        {
            string? hit = string.IsNullOrWhiteSpace(tool.Folder)
                ? null
                : FindFirst(files, Path.Combine(root, tool.Folder), tool.Executables);
            result.Add(hit ?? FindFirst(files, root, tool.Executables));
        }

        return result;
    }

    private static string? FindFirst(List<string> files, string dir, IEnumerable<string> patterns)
    {
        string prefix = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        List<string> inScope = files.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                                f.AsSpan(prefix.Length).Count(Path.DirectorySeparatorChar) <= SearchDepth)
            .ToList();

        foreach (string pattern in patterns)
        {
            // Shortest path wins: prefer the copy nearest the tool folder root.
            string? best = inScope
                .Where(f => FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(f.AsSpan()), ignoreCase: true))
                .MinBy(f => f.Length);
            if (best != null)
            {
                return best;
            }
        }

        return null;
    }
}
