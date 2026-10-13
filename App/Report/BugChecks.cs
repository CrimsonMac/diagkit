using System;
using System.Collections.Generic;
using System.Globalization;

namespace DiagKit.Report;

public enum CrashCause
{
    Unknown,
    Driver,
    Memory,
    Cpu,
    Storage,
    Graphics,
    System,
}

public sealed record BugCheckInfo(string Name, CrashCause Cause, string Meaning);

public static class BugChecks
{
    private static readonly Dictionary<ulong, BugCheckInfo> Known = new()
    {
        [0x0A] = new("IRQL_NOT_LESS_OR_EQUAL", CrashCause.Driver, "A driver touched memory it should not have. Usually a faulty driver, sometimes unstable RAM."),
        [0x1A] = new("MEMORY_MANAGEMENT", CrashCause.Memory, "Windows found its memory bookkeeping corrupted. Most often unstable or failing RAM."),
        [0x1E] = new("KMODE_EXCEPTION_NOT_HANDLED", CrashCause.Driver, "A kernel driver crashed with an unhandled error."),
        [0x3B] = new("SYSTEM_SERVICE_EXCEPTION", CrashCause.Driver, "A driver crashed while handling a system call. Graphics and antivirus drivers are common culprits."),
        [0x4E] = new("PFN_LIST_CORRUPT", CrashCause.Memory, "The list Windows uses to track physical memory got corrupted. Points strongly at RAM."),
        [0x50] = new("PAGE_FAULT_IN_NONPAGED_AREA", CrashCause.Memory, "Code referenced memory that does not exist. Faulty RAM or a buggy driver."),
        [0x7A] = new("KERNEL_DATA_INPAGE_ERROR", CrashCause.Storage, "Windows could not read data back from the drive or page file. Check the drive and its cable."),
        [0x77] = new("KERNEL_STACK_INPAGE_ERROR", CrashCause.Storage, "Windows could not read kernel data from the page file. Usually a drive or cable problem."),
        [0x7E] = new("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", CrashCause.Driver, "A system thread crashed, almost always inside a driver."),
        [0x7F] = new("UNEXPECTED_KERNEL_MODE_TRAP", CrashCause.Cpu, "The CPU raised a trap the kernel could not handle. Unstable overclock, bad RAM, or overheating."),
        [0x9F] = new("DRIVER_POWER_STATE_FAILURE", CrashCause.Driver, "A driver did not respond while the PC was going to sleep or waking up."),
        [0xA0] = new("INTERNAL_POWER_ERROR", CrashCause.Driver, "Power management failed, often during sleep or hibernation."),
        [0xC2] = new("BAD_POOL_CALLER", CrashCause.Driver, "A driver misused kernel memory."),
        [0x19] = new("BAD_POOL_HEADER", CrashCause.Driver, "Kernel memory was corrupted, usually by a driver, sometimes by bad RAM."),
        [0xC5] = new("DRIVER_CORRUPTED_EXPOOL", CrashCause.Driver, "A driver corrupted kernel memory."),
        [0xD1] = new("DRIVER_IRQL_NOT_LESS_OR_EQUAL", CrashCause.Driver, "A driver accessed invalid memory. Network, audio and graphics drivers are frequent causes."),
        [0xEF] = new("CRITICAL_PROCESS_DIED", CrashCause.System, "A core Windows process died. Can be file corruption, storage errors or malware."),
        [0xF4] = new("CRITICAL_OBJECT_TERMINATION", CrashCause.Storage, "A critical process ended unexpectedly. Commonly a failing drive or loose cable."),
        [0xFC] = new("ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", CrashCause.Driver, "A driver tried to run code from data memory."),
        [0x101] = new("CLOCK_WATCHDOG_TIMEOUT", CrashCause.Cpu, "A CPU core stopped responding. Typical of unstable CPU overclocks or undervolts."),
        [0x109] = new("CRITICAL_STRUCTURE_CORRUPTION", CrashCause.Memory, "Kernel code or data was modified. Bad RAM, a misbehaving driver, or tampering."),
        [0x116] = new("VIDEO_TDR_FAILURE", CrashCause.Graphics, "The graphics driver stopped responding and could not recover. Driver, GPU overclock or a failing card."),
        [0x117] = new("VIDEO_TDR_TIMEOUT_DETECTED", CrashCause.Graphics, "The graphics driver timed out."),
        [0x119] = new("VIDEO_SCHEDULER_INTERNAL_ERROR", CrashCause.Graphics, "The graphics scheduler hit an error. Usually the GPU driver."),
        [0x124] = new("WHEA_UNCORRECTABLE_ERROR", CrashCause.Cpu, "The hardware itself reported a fatal error: CPU, RAM, or PCIe. Undo overclocks and undervolts first."),
        [0x133] = new("DPC_WATCHDOG_VIOLATION", CrashCause.Driver, "A driver took too long. Storage controller drivers and SSD firmware are common causes."),
        [0x139] = new("KERNEL_SECURITY_CHECK_FAILURE", CrashCause.Driver, "Kernel data failed an integrity check. Usually a driver, sometimes RAM."),
        [0x13A] = new("KERNEL_MODE_HEAP_CORRUPTION", CrashCause.Driver, "A driver corrupted the kernel heap."),
        [0x144] = new("BUGCODE_USB3_DRIVER", CrashCause.Driver, "The USB 3 driver stack crashed. Try another port or remove the device involved."),
        [0x154] = new("UNEXPECTED_STORE_EXCEPTION", CrashCause.Storage, "The memory compression store hit an error. Often storage, sometimes RAM."),
        [0x18B] = new("SECURE_KERNEL_ERROR", CrashCause.System, "The secure kernel (virtualization-based security) failed."),
        [0x1D8] = new("UNSUPPORTED_PROCESSOR", CrashCause.Cpu, "This CPU is not supported by the installed Windows version."),
        [0xC000021A] = new("STATUS_SYSTEM_PROCESS_TERMINATED", CrashCause.System, "A critical user-mode process failed. Often a broken update or system file corruption."),
    };

    /// <summary>Turns "0x0000003b" into "0x3B".</summary>
    public static string Normalize(string hex)
    {
        if (TryParse(hex, out ulong v))
        {
            return "0x" + v.ToString("X", CultureInfo.InvariantCulture);
        }

        return hex;
    }

    public static bool TryParse(string? hex, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        string s = hex!.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    public static BugCheckInfo? Lookup(string? code)
    {
        if (!TryParse(code, out ulong v))
        {
            return null;
        }

        // Some reports use the 0x1000007E style "extended" codes.
        if (!Known.TryGetValue(v, out BugCheckInfo? info) && v > 0x10000000 && v < 0x20000000)
        {
            Known.TryGetValue(v - 0x10000000, out info);
        }

        return info;
    }

    public static string Describe(string? code)
    {
        BugCheckInfo? info = Lookup(code);
        return info == null ? (code ?? "unknown") : $"{info.Name} ({Normalize(code!)})";
    }
}
