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
        MoveLegacyManifest();
        EnsureToolsRoot();

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
                warning = @"Could not write Tools\tools.json (" + ex.Message + "). Using the built-in tool list.";
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
        Upgrade(manifest);

        return new ManifestLoadResult { Manifest = manifest, Warning = warning };
    }

    /// <summary>
    /// Applies built-in list changes to a tools.json written by an older DiagKit, in memory only, so the
    /// user's own edits in the file are kept.
    /// </summary>
    private static void Upgrade(ToolManifest manifest)
    {
        if (manifest.Version < 2)
        {
            // Version 2: PawnIO is installed from the health check, not launched as a tile.
            foreach (ToolDefinition t in manifest.Tools.Where(t => t.Id.Equals("pawnio", StringComparison.OrdinalIgnoreCase)))
            {
                t.Hidden = true;
            }
        }

        if (manifest.Version < 3)
        {
            // Version 3: new tools, and the driver services, temp files and registry keys each tool leaves behind.
            List<ToolDefinition> defaults = JsonSerializer.Deserialize<ToolManifest>(ReadDefaultManifestText(), JsonOptions)?.Tools ?? new List<ToolDefinition>();
            foreach (ToolDefinition d in defaults)
            {
                ToolDefinition? mine = manifest.Tools.FirstOrDefault(t => t.Id.Equals(d.Id, StringComparison.OrdinalIgnoreCase));
                if (mine == null)
                {
                    manifest.Tools.Add(d);
                    continue;
                }

                AddMissing(mine.RegistryTraces, d.RegistryTraces);
                AddMissing(mine.RegistryParents, d.RegistryParents);
                AddMissing(mine.Services, d.Services);
                AddMissing(mine.Files, d.Files);
            }
        }
    }

    private static void AddMissing(List<string> list, IEnumerable<string> extra)
    {
        foreach (string e in extra.Where(e => !list.Contains(e, StringComparer.OrdinalIgnoreCase)))
        {
            list.Add(e);
        }
    }

    /// <summary>
    /// Creates Tools\ only. A tool's own folder is created when it is first needed (by setup, or by "open folder"
    /// on a missing tool), so tools that were left out of the kit leave no empty folders behind.
    /// </summary>
    private static void EnsureToolsRoot()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ToolsRoot);
        }
        catch
        {
            // Read-only media: resolution still works for whatever exists.
        }
    }

    /// <summary>Kits set up before 1.1 kept tools.json next to DiagKit.exe; it now lives in Tools\.</summary>
    private static void MoveLegacyManifest()
    {
        try
        {
            if (File.Exists(AppPaths.LegacyManifestPath) && !File.Exists(AppPaths.ManifestPath))
            {
                Directory.CreateDirectory(AppPaths.ToolsRoot);
                File.Move(AppPaths.LegacyManifestPath, AppPaths.ManifestPath);
            }
        }
        catch
        {
            // Read-only kit: the old file stays where it is and the built-in list is used.
        }
    }

    /// <summary>How many folder levels below the folder being searched an executable may sit.</summary>
    private const int SearchDepth = 3;

    /// <summary>
    /// Finds the executable for each tool: first in its own folder, then anywhere under tools\
    /// (so a zip extracted into a differently named folder still works). The folder tree is read once
    /// for all tools; on a slow USB stick the directory walk is the expensive part.
    /// </summary>
    public static List<string?> ResolveAll(IReadOnlyList<ToolDefinition> tools)
    {
        string root = AppPaths.ToolsRoot;
        var files = new List<string>();
        if (Directory.Exists(root))
        {
            // One extra level, so a tool's own folder (tools\<folder>) can be searched SearchDepth levels deep too.
            Walk(new DirectoryInfo(root), SearchDepth + 1, files);
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

    /// <summary>Collects files up to <paramref name="depth"/> folder levels down, skipping unreadable folders and system files.</summary>
    private static void Walk(DirectoryInfo dir, int depth, List<string> files)
    {
        try
        {
            foreach (FileSystemInfo entry in dir.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.System) != 0)
                {
                    continue;
                }

                if (entry is DirectoryInfo sub)
                {
                    // Junctions and symlinks are skipped so a link loop cannot make the walk endless.
                    if (depth > 0 && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        Walk(sub, depth - 1, files);
                    }
                }
                else
                {
                    files.Add(entry.FullName);
                }
            }
        }
        catch (Exception)
        {
            // Unreadable folder: whatever is in it shows as missing.
        }
    }

    private static string? FindFirst(List<string> files, string dir, IEnumerable<string> patterns)
    {
        string prefix = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        List<string> inScope = files.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                                Depth(f, prefix.Length) <= SearchDepth)
            .ToList();

        foreach (string pattern in patterns)
        {
            // Shortest path wins: prefer the copy nearest the tool folder root.
            string? best = inScope
                .Where(f => Wildcard.IsMatch(pattern, Path.GetFileName(f)))
                .OrderBy(f => f.Length)
                .FirstOrDefault();
            if (best != null)
            {
                return best;
            }
        }

        return null;
    }

    private static int Depth(string path, int start)
    {
        int count = 0;
        for (int i = start; i < path.Length; i++)
        {
            if (path[i] == Path.DirectorySeparatorChar)
            {
                count++;
            }
        }

        return count;
    }
}
