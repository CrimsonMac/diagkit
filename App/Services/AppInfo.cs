namespace DiagKit.Services;

public static class AppInfo
{
    /// <summary>Product version from the assembly (set by &lt;Version&gt; in the project file), e.g. "1.0.0".</summary>
    public static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}
