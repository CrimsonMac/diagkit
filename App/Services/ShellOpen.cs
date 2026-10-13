using System;
using System.Diagnostics;
using System.IO;

namespace DiagKit.Services;

/// <summary>
/// DiagKit runs elevated. Opening a URL or an HTML file with ShellExecute from an elevated process
/// would start the browser elevated too. Handing the path to explorer.exe lets the already-running
/// (non-elevated) shell open it instead.
/// </summary>
public static class ShellOpen
{
    public static bool Open(string pathOrUrl, out string? error)
    {
        error = null;
        try
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var psi = new ProcessStartInfo(explorer)
            {
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(pathOrUrl);
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool Open(string pathOrUrl) => Open(pathOrUrl, out _);

    /// <summary>Opens an Explorer window with the file selected.</summary>
    public static bool Reveal(string filePath)
    {
        try
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var psi = new ProcessStartInfo(explorer, "/select,\"" + filePath + "\"")
            {
                UseShellExecute = false,
            };
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
