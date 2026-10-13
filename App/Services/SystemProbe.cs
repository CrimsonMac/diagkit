using System;
using System.Security.Principal;
using Microsoft.Win32;

namespace DiagKit.Services;

public static class SystemProbe
{
    public static bool IsElevated()
    {
        try
        {
            using WindowsIdentity id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// LibreHardwareMonitor 0.9.5+ reads CPU temperatures through the PawnIO driver, which has to be
    /// installed on the PC. LHM itself checks this uninstall key; the service key is a fallback.
    /// </summary>
    public static bool IsPawnIoInstalled() => Kit.PawnIo.IsInstalled();

    /// <summary>Memory Integrity (HVCI) blocks some older kernel drivers that diagnostic tools ship.</summary>
    public static bool? IsMemoryIntegrityEnabled()
    {
        try
        {
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            if (key?.GetValue("Enabled") is int v)
            {
                return v == 1;
            }

            return key == null ? false : null;
        }
        catch
        {
            return null;
        }
    }

    public static string MachineName => Environment.MachineName;
}
