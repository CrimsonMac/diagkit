using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace DiagKit.Kit;

/// <summary>The PawnIO driver LibreHardwareMonitor needs for CPU temperatures. Installed on the PC, not in the kit.</summary>
public static class PawnIo
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    /// <summary>LibreHardwareMonitor checks the uninstall key; the service key is a fallback.</summary>
    public static bool IsInstalled()
    {
        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? uninstall = hklm.OpenSubKey(UninstallKey);
            if (uninstall != null)
            {
                return true;
            }

            using RegistryKey? service = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return service != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs PawnIO's own silent uninstaller. True when PawnIO is gone afterwards.</summary>
    public static bool Uninstall(out string message)
    {
        if (!IsInstalled())
        {
            message = "PawnIO was already removed";
            return true;
        }

        string? command = null;
        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = hklm.OpenSubKey(UninstallKey);
            command = key?.GetValue("QuietUninstallString") as string;
            if (string.IsNullOrWhiteSpace(command) && key?.GetValue("UninstallString") is string plain)
            {
                command = plain + " -silent";
            }
        }
        catch
        {
            // Handled below.
        }

        if (string.IsNullOrWhiteSpace(command) || !SplitCommand(command!, out string exe, out string args))
        {
            message = "PawnIO has no uninstaller registered; remove it in Settings > Apps";
            return false;
        }

        try
        {
            using Process? p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
            if (p != null && !p.WaitForExit(120_000))
            {
                message = "PawnIO's uninstaller did not finish in time";
                return false;
            }
        }
        catch (Exception ex)
        {
            message = "PawnIO's uninstaller could not run: " + ex.Message;
            return false;
        }

        bool gone = !IsInstalled();
        message = gone ? "Removed the PawnIO driver" : "PawnIO's uninstaller ran but the driver is still installed";
        return gone;
    }

    /// <summary>Splits "\"C:\path\app.exe\" -a -b" or "C:\app.exe -a" into program and arguments.</summary>
    internal static bool SplitCommand(string command, out string exe, out string args)
    {
        command = command.Trim();
        if (command.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = command.IndexOf('"', 1);
            if (end < 0)
            {
                exe = args = "";
                return false;
            }

            exe = command.Substring(1, end - 1);
            args = command.Substring(end + 1).Trim();
            return true;
        }

        int space = command.IndexOf(' ');
        exe = space < 0 ? command : command.Substring(0, space);
        args = space < 0 ? "" : command.Substring(space + 1).Trim();
        return true;
    }
}
