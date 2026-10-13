using System;
using System.IO;

namespace DiagKit.Services;

public static class AppPaths
{
    /// <summary>Folder containing DiagKit.exe (correct for single-file publishes too).</summary>
    public static string BaseDir { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public static string ToolsRoot => Path.Combine(BaseDir, "tools");
    public static string ManifestPath => Path.Combine(BaseDir, "tools.json");
    public static string ErrorLogPath => Path.Combine(BaseDir, "diagkit-error.log");

    private static string? _reportsRoot;

    /// <summary>
    /// reports\ next to the exe when that is writable (the normal USB case),
    /// otherwise Documents\DiagKit Reports on the current machine.
    /// </summary>
    public static string ReportsRoot
    {
        get
        {
            if (_reportsRoot != null)
            {
                return _reportsRoot;
            }

            string preferred = Path.Combine(BaseDir, "reports");
            if (IsWritable(preferred))
            {
                _reportsRoot = preferred;
            }
            else
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                _reportsRoot = Path.Combine(docs, "DiagKit Reports");
                Directory.CreateDirectory(_reportsRoot);
            }

            return _reportsRoot;
        }
    }

    public static bool ReportsAreOnKit => string.Equals(
        ReportsRoot, Path.Combine(BaseDir, "reports"), StringComparison.OrdinalIgnoreCase);

    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Appends to diagkit-error.log next to the exe, or in %TEMP% on a read-only kit. Returns the path written, or null.</summary>
    public static string? LogError(string context, Exception ex)
    {
        string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
        foreach (string path in new[] { ErrorLogPath, Path.Combine(Path.GetTempPath(), "diagkit-error.log") })
        {
            try
            {
                File.AppendAllText(path, text);
                return path;
            }
            catch
            {
                // Try the next location.
            }
        }

        return null;
    }
}
