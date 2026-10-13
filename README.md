# DiagKit

A portable launcher for Windows diagnostic tools, plus a health check that reads drive health, crash history, hardware errors, temperatures and startup entries and explains what it finds in plain language.

Built for a USB stick: one exe, a `tools` folder, and a `reports` folder that travels with it.

## Build

You need the [.NET SDK](https://dotnet.microsoft.com/download) on the machine you build on. DiagKit targets .NET Framework 4.8, which is part of Windows 10 (1903 and later) and Windows 11, so the PCs you diagnose need nothing extra.

```powershell
.\build.ps1 -Output E:\DiagKit        # build onto the stick and download every tool (hash/signature verified)
.\fetch-tools.ps1 -Kit E:\DiagKit     # later: update the tools without rebuilding
```

Every tool has fallback sources, so one site being down doesn't stop setup. Run `fetch-tools.ps1` again whenever you like; it only downloads tools that have a new release, and `-Check` lists available updates without downloading. See [TOOLS.md](TOOLS.md) for sources and how each download is verified. The kit layout:

```
DiagKit\
  DiagKit.exe            single-file app (runs as administrator); everything it needs is inside
  tools.json             tool list, created on first run, editable
  tools\<Tool>\...       the portable tools
  reports\<PC>_<date>\   report.html, report.json, raw\
```

For development, open `DiagKit.slnx` in Visual Studio 2026 or Rider, or run `dotnet run --project App`. Visual Studio has to run elevated, because the app manifest requires administrator rights.

## Using it

**Tools** (Ctrl+1). Click a tile to launch the tool.
- Ctrl+F searches; Enter launches the first match and Esc clears the search.
- Missing tools show where their file goes, and clicking one opens that folder.
- Hover a tile for its folder button, which is handy for TestMem5 configs (anta777's Extreme loads by default; Absolut is in `bin`).
- DiagKit explains launch failures: Defender quarantine, Smart App Control, a blocked driver, or a tool that closes immediately.
- Registry keys a tool creates (Sysinternals EULA flags, GPU-Z settings) are removed when it exits, but only if they did not exist before. The PC owner's own settings are never touched.

**Health** (Ctrl+2, F5 from anywhere) runs these steps:

| Step | Source | Needs |
|---|---|---|
| System overview | WMI, registry | nothing |
| Sensor driver (when missing) | PawnIO setup, run silently | ticked option; installs a signed driver |
| Temperatures at idle | LibreHardwareMonitor | PawnIO for CPU temps; GPU works without it |
| Memory | WMI | nothing |
| Free space | .NET | nothing |
| Drive health | Windows storage WMI + smartctl JSON | smartctl for full SMART |
| Event logs (30 days) | WHEA, Kernel-Power 41, bug checks, disk/NTFS/NVMe errors, display driver resets, app and service crashes | nothing |
| Crash dumps | BlueScreenView `/sxml` | BlueScreenView |
| Devices | Device Manager problem codes | nothing |
| Battery | WMI battery capacity | laptop |
| Startup entries (optional) | `autorunsc64 -s -m` | autorunsc64.exe |
| CPU load test (optional, 30 s or 2 min) | all-core vector load; temperature, clock and power sampled each second | nothing (clock falls back to perf counters) |

Steps in the same phase run in parallel, and each has its own timeout. A hung or failing step is marked as such and the rest of the report still completes.

The rules turn the data into findings, each with a severity, a plain explanation, **what to do**, and the evidence behind it. Thresholds are conservative and the wording is hedged where the data can't prove a cause. For example, `WHEA_UNCORRECTABLE_ERROR` dumps always name `ntoskrnl.exe`, so the report says the error came from the hardware rather than blaming the kernel. Clocks dropping on a laptop under load is described as power-limit behaviour unless temperatures are also high.

**Reports** (Ctrl+3) lists saved checks. Each one is a folder containing:
- `report.html`: one self-contained file with no scripts or external assets, which works on a phone and follows light/dark mode. It's the one to send someone.
- `report.json`: the full structured data.
- `raw\`: every tool's original output.

`Examples\sample-report.html` shows what a report for a troubled PC looks like.

## Why it opens fast

- **No runtime to load.** DiagKit runs on the .NET Framework that ships with Windows, whose WPF is already precompiled. The exe is about 5 MB instead of a 150 MB self-contained bundle, which matters most on a slow USB stick.
- **One file, no extraction.** The libraries DiagKit uses are embedded in the exe and loaded from memory, so nothing is unpacked to `%TEMP%` and no trace is left on the PC.
- **No UI framework dependency.** The theme is plain WPF styles, so no third-party resource dictionaries are parsed at startup.
- **The window renders before any disk work.** The manifest is read, tools are searched recursively and exe icons are extracted on background threads, and the icons are cached per session.
- **Lazy work.** Pages are created on first visit. The sensor driver library is only loaded when a health check starts.
- **Collectors never touch the UI thread.** The window stays responsive during a load test because the load threads run at below-normal priority.

## Testing status

This was compiled and tested on Linux against the real WPF, WMI, EventLog and LibreHardwareMonitor 0.9.6 assemblies. That covers:
- the full build including XAML, with zero warnings;
- all 135 XAML bindings, checked by reflection against the view models;
- the rules engine, against synthetic healthy, failing and empty reports;
- tool discovery, manifest error handling, runner timeouts and cancellation, and the JSON round-trip.

**It has not been run on Windows yet.** Windows-only behaviour is written against documented APIs but unverified until a first real run. That includes:
- WMI queries and event log XPath;
- `smartctl`, BlueScreenView and `autorunsc` output parsing;
- the window chrome;
- sensor readings.

If DiagKit fails to start, it shows an error box and writes `diagkit-error.log` next to the exe (or in `%TEMP%` when the kit is read-only). If even that doesn't appear, check Event Viewer > Windows Logs > Application for "Application Error" and ".NET Runtime" entries. If something misbehaves later, `diagkit-error.log` has the stack trace, and each report's `raw\` folder has the tool output the parsers saw.

## Project layout

```
App\
  App.xaml, MainWindow.xaml     shell: title bar, navigation rail, notices
  Themes\Theme.xaml             palette, type, LED control, all control styles
  Views\                        Tools, Health (setup/progress/results), Report, History
  ViewModels\                   WPF-free view models
  Services\                     tool discovery, launching, registry cleanup, elevation/PawnIO probes
  Report\
    Collectors\                 one class per health-check step
    Rules\HealthRules.cs        findings: severity, explanation, action, evidence
    ReportRunner.cs             phases, parallelism, timeouts, cancellation
    ReportFacts.cs              verdict/spec sheet/tables shared by app and HTML
    HtmlReportWriter.cs         self-contained HTML export
  tools.default.json            built-in tool list, written out as tools.json
```
