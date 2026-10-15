using System;
using System.Collections.Generic;

namespace DiagKit.Report;

public enum Severity
{
    Ok = 0,
    Info = 1,
    Warning = 2,
    Critical = 3,
}

public sealed class Finding
{
    public string Id { get; set; } = "";
    public Severity Severity { get; set; }
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? Action { get; set; }
    public List<string> Evidence { get; set; } = new();
}

public sealed class DiagnosticReport
{
    public int SchemaVersion { get; set; } = 1;
    public string AppVersion { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public double DurationSeconds { get; set; }
    public string MachineName { get; set; } = "";

    /// <summary>What a Quick action or Custom diagnosis covered; null for a full health check.</summary>
    public string? Scope { get; set; }

    public SystemInfo System { get; set; } = new();
    public MemoryInfo Memory { get; set; } = new();
    public List<DiskInfo> Disks { get; set; } = new();
    public List<VolumeInfo> Volumes { get; set; } = new();
    public EventSummary? Events { get; set; }
    public ReliabilitySummary? Reliability { get; set; }
    public List<CrashDump> CrashDumps { get; set; } = new();
    public bool CrashDumpsAnalyzed { get; set; }
    public AutorunSummary? Autoruns { get; set; }
    public List<ProblemDevice> ProblemDevices { get; set; } = new();
    public bool DevicesChecked { get; set; }
    public BatteryInfo? Battery { get; set; }
    public SensorSnapshot? Sensors { get; set; }
    public StressResult? Stress { get; set; }
    public List<Finding> Findings { get; set; } = new();
    public List<CollectorRecord> Collectors { get; set; } = new();
}

public sealed class CollectorRecord
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Message { get; set; }
    public double Seconds { get; set; }
}

public sealed class SystemInfo
{
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string ChassisKind { get; set; } = "Unknown";
    public string? Motherboard { get; set; }
    public string? BiosVersion { get; set; }
    public DateTime? BiosDate { get; set; }
    public string? OsName { get; set; }
    public string? OsDisplayVersion { get; set; }
    public int OsBuild { get; set; }
    public int OsUbr { get; set; }
    public DateTime? OsInstallDate { get; set; }
    public DateTime? LastBoot { get; set; }
    public string? CpuName { get; set; }
    public int CpuCores { get; set; }
    public int CpuThreads { get; set; }
    public int CpuMaxClockMhz { get; set; }
    public List<GpuInfo> Gpus { get; set; } = new();
    public long TotalMemoryBytes { get; set; }
    public bool? PendingReboot { get; set; }
    public bool? MemoryIntegrity { get; set; }
    public bool PawnIoInstalled { get; set; }
    public bool? SecureBoot { get; set; }
}

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public string? DriverVersion { get; set; }
    public DateTime? DriverDate { get; set; }
}

public sealed class MemoryInfo
{
    public List<MemoryModule> Modules { get; set; } = new();
    public long TotalBytes { get; set; }
    public long AvailableBytes { get; set; }
    public int SlotCount { get; set; }
}

public sealed class MemoryModule
{
    public string? Slot { get; set; }
    public string? Manufacturer { get; set; }
    public string? PartNumber { get; set; }
    public long CapacityBytes { get; set; }
    public int RatedSpeed { get; set; }
    public int ConfiguredSpeed { get; set; }
    public string? MemoryType { get; set; }
    public int? PartNumberSpeedHint { get; set; }
}

public sealed class DiskInfo
{
    public int? Number { get; set; }
    public string Model { get; set; } = "";
    public string? Serial { get; set; }
    public string? Firmware { get; set; }
    public string? BusType { get; set; }
    public string? MediaType { get; set; }
    public long SizeBytes { get; set; }
    public List<string> DriveLetters { get; set; } = new();
    public string Source { get; set; } = "windows";
    public string? WindowsHealth { get; set; }
    public bool? SmartPassed { get; set; }
    public int? SmartExitStatus { get; set; }
    public int? Temperature { get; set; }
    public long? PowerOnHours { get; set; }
    public long? ReallocatedSectors { get; set; }
    public long? PendingSectors { get; set; }
    public long? UncorrectableSectors { get; set; }
    public long? ReportedUncorrectable { get; set; }
    public long? CrcErrors { get; set; }
    public int? LifeLeftPercent { get; set; }
    public int? PercentageUsed { get; set; }
    public int? AvailableSpare { get; set; }
    public int? AvailableSpareThreshold { get; set; }
    public long? MediaErrors { get; set; }
    public int? CriticalWarning { get; set; }
    public long? UnsafeShutdowns { get; set; }
    public string? RawFile { get; set; }
}

public sealed class VolumeInfo
{
    public string Letter { get; set; } = "";
    public string? Label { get; set; }
    public string? FileSystem { get; set; }
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public bool IsSystem { get; set; }

    public double FreePercent => TotalBytes > 0 ? FreeBytes * 100.0 / TotalBytes : 0;
}

public sealed class CountItem
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public int Count { get; set; }
    public DateTime? LastSeen { get; set; }
    public string? Extra { get; set; }
}

public sealed class EventSummary
{
    public int WindowDays { get; set; } = 30;
    public List<CountItem> Whea { get; set; } = new();
    public int UnexpectedShutdowns { get; set; }
    public int UnexpectedWithBugcheck { get; set; }
    public int PowerButtonHolds { get; set; }
    public DateTime? LastUnexpectedShutdown { get; set; }
    public List<BugCheckEvent> BugChecks { get; set; } = new();
    public List<CountItem> DiskErrors { get; set; } = new();
    public List<CountItem> GpuResets { get; set; } = new();
    public List<CountItem> AppCrashes { get; set; } = new();
    public List<CountItem> ServiceCrashes { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// What Windows' Reliability Monitor recorded that the event-log step does not already cover: programs that hung,
/// updates and installs that failed, and the stability index Windows computes from all of it.
/// </summary>
public sealed class ReliabilitySummary
{
    public int WindowDays { get; set; } = 30;

    /// <summary>Windows' own 1 to 10 score, newest value; null when Reliability Monitor has no data.</summary>
    public double? StabilityIndex { get; set; }

    public double? LowestStabilityIndex { get; set; }
    public DateTime? LowestAt { get; set; }

    /// <summary>Per program: Label is the exe, Extra its path.</summary>
    public List<CountItem> AppHangs { get; set; } = new();

    /// <summary>Windows updates whose last attempt failed and that have not installed since. Extra is the last error code.</summary>
    public List<CountItem> FailedUpdates { get; set; } = new();

    /// <summary>Microsoft Store app updates, kept apart because they usually fail only while the app is open.</summary>
    public List<CountItem> FailedStoreUpdates { get; set; } = new();

    /// <summary>Windows Installer (MSI) installs that ended with an error. Extra is the status code.</summary>
    public List<CountItem> FailedInstalls { get; set; } = new();

    public int RecordCount { get; set; }
}

public sealed class BugCheckEvent
{
    public DateTime? Time { get; set; }
    public string Code { get; set; } = "";
    public string? Raw { get; set; }
}

public sealed class CrashDump
{
    public string File { get; set; } = "";
    public DateTime? Time { get; set; }
    public string? BugCheckCode { get; set; }
    public string? BugCheckString { get; set; }
    public string? CausedByDriver { get; set; }
    public string? DriverDescription { get; set; }
    public string? DriverCompany { get; set; }
}

public sealed class AutorunEntry
{
    public string Category { get; set; } = "";
    public string Entry { get; set; } = "";
    public string? Location { get; set; }
    public string? ImagePath { get; set; }
    public string? Signer { get; set; }
    public string? Company { get; set; }
    public bool Enabled { get; set; }
    public bool Verified { get; set; }
    public bool FileMissing { get; set; }
}

public sealed class AutorunSummary
{
    public int TotalEntries { get; set; }
    public int LogonEntries { get; set; }
    public List<AutorunEntry> Unsigned { get; set; } = new();
    public List<AutorunEntry> Missing { get; set; } = new();
    public List<AutorunEntry> UnusualLocations { get; set; } = new();
}

public sealed class ProblemDevice
{
    public string Name { get; set; } = "";
    public string? DeviceId { get; set; }
    public string? Class { get; set; }
    public int ErrorCode { get; set; }
    public string ErrorText { get; set; } = "";
}

public sealed class BatteryInfo
{
    public long DesignCapacity { get; set; }
    public long FullChargeCapacity { get; set; }
    public int? CycleCount { get; set; }

    /// <summary>Where the capacities came from: "wmi", or "batteryreport" when the firmware only reports them there.</summary>
    public string Source { get; set; } = "wmi";

    /// <summary>Windows' estimate of active use on a full charge, now and when the battery was new (powercfg /batteryreport).</summary>
    public int? RuntimeMinutes { get; set; }

    public int? DesignRuntimeMinutes { get; set; }

    /// <summary>Full-charge capacity over time, oldest first, since the battery was last replaced.</summary>
    public List<BatteryCapacityPoint> History { get; set; } = new();

    public double WearPercent => DesignCapacity > 0
        ? Math.Max(0, 100.0 - FullChargeCapacity * 100.0 / DesignCapacity)
        : 0;
}

public sealed class BatteryCapacityPoint
{
    public DateTime Date { get; set; }
    public long FullChargeCapacity { get; set; }
    public long DesignCapacity { get; set; }
}

public sealed class NamedReading
{
    public string Name { get; set; } = "";
    public double Value { get; set; }
    public string Unit { get; set; } = "";
}

public sealed class GpuSensors
{
    public string Name { get; set; } = "";
    public double? Temperature { get; set; }
    public double? HotSpot { get; set; }
    public double? Load { get; set; }
    public double? Power { get; set; }
    public double? FanRpm { get; set; }
}

public sealed class SensorSnapshot
{
    public bool CpuTemperatureAvailable { get; set; }
    public double? CpuPackageTemp { get; set; }
    public double? CpuMaxCoreTemp { get; set; }
    public double? CpuClockMhz { get; set; }
    public double? CpuPowerW { get; set; }
    public double? CpuLoad { get; set; }
    public List<GpuSensors> Gpus { get; set; } = new();
    public List<NamedReading> Fans { get; set; } = new();
    public List<NamedReading> BoardTemps { get; set; } = new();
    public string? Note { get; set; }
}

public sealed class StressSample
{
    public double Seconds { get; set; }
    public double? CpuTemp { get; set; }
    public double? ClockMhz { get; set; }
    public double? PowerW { get; set; }
}

public sealed class StressResult
{
    public int DurationSeconds { get; set; }
    public int Threads { get; set; }
    public bool Completed { get; set; }
    public List<StressSample> Samples { get; set; } = new();
    public double? MaxTemp { get; set; }
    public double? EarlyClockMhz { get; set; }
    public double? LateClockMhz { get; set; }
    public double? ClockDropPercent { get; set; }
    public double? MaxPowerW { get; set; }
    public string ClockSource { get; set; } = "";
}
