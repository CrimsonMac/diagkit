using System.Collections.Generic;

namespace DiagKit.Models;

/// <summary>Visual state of a status LED. Shared by tools, health-check steps and findings.</summary>
public enum LedState
{
    Off,
    Ok,
    Info,
    Warning,
    Critical,
    Busy,
}

public sealed class ToolManifest
{
    public int Version { get; set; } = 1;
    public List<ToolDefinition> Tools { get; set; } = new();
}

public sealed class ToolDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Other";
    public string Description { get; set; } = "";
    public string Folder { get; set; } = "";
    public List<string> Executables { get; set; } = new();
    public string? Arguments { get; set; }
    public List<string> RegistryTraces { get; set; } = new();
    public List<string> RegistryParents { get; set; } = new();

    /// <summary>Driver service names (wildcards allowed) the tool may create; removed after it exits if they are new.</summary>
    public List<string> Services { get; set; } = new();

    /// <summary>Files the tool leaves outside its folder ("%TEMP%\x*.sys"); removed after it exits if created by that run.</summary>
    public List<string> Files { get; set; } = new();

    public bool Hidden { get; set; }
    public string? Homepage { get; set; }
}
