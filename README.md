# DiagKit

A portable launcher for Windows diagnostic tools, plus a health check that reads drive health, crash history, hardware errors, temperatures and startup entries and explains what it finds in plain language.

It is built for a USB stick: one small exe, the tools next to it, and reports that travel with the stick. When you are done with a PC, Uninstall.exe removes what DiagKit and its tools left on it.

## Getting a kit

Run **setup.exe** (see [Building](#building) for how to get it).

1. **Pick the location.** USB sticks are listed as one-click choices; **Change…** picks any folder. Setup creates `diagkit.<version>` inside it. Pick a folder that already holds a kit, or the kit itself, and setup updates that kit instead.
2. **Tick the tools.** All of them are ticked for a new kit, about 110 MB in total. The ones the health check needs can't be unticked. On an existing kit, unticking a tool removes it, and setup remembers that choice next time.
3. **Install.** Three downloads run at a time. Every file is checked against its publisher's hash or signature before it goes into the kit, and each tool has fallback sources in case a site is down. See [TOOLS.md](TOOLS.md) for the details.

Run setup again whenever you like: it only downloads tools that have a new release. Updating a kit renames the folder to the new version. Tools you added to the kit yourself, and your reports, are kept.

Setup needs no administrator rights and leaves nothing on the PC it runs on.

```
setup.exe [/target <folder>] [/tools name,name] [/force] [/quiet]
  /target   where the kit goes: an existing kit, or a folder to create diagkit.<version> in
  /tools    update only these tools and leave the rest of the kit as it is
  /force    download every tool again, even when it is up to date
  /quiet    no window; logs to setup.log next to setup.exe; exit code 0 when every tool installed
```

The kit:

```
diagkit.1.1.0\
  DiagKit.exe           the app (runs as administrator); one file, about 4.5 MB
  Uninstall.exe         cleans this PC and/or removes the kit
  Tools\                one folder per tool, plus tools.json (the tool list, editable)
  Reports\<PC>_<date>\  report.html, report.json, raw\
  .diagkit              hidden record of what DiagKit installed, for Uninstall.exe
```

DiagKit runs on .NET Framework 4.8, which is part of Windows 10 (1903 and later) and Windows 11, so the PCs you diagnose need nothing extra.

## Removing DiagKit

**Uninstall.exe**, in the kit or behind the button at the bottom of DiagKit's navigation rail, offers two modes:

- **Remove DiagKit completely.** Deletes the kit and cleans this PC. Files you added to the kit are listed and kept, and the kit folder is only removed once it is empty.
- **Only clean this PC.** Cleans this PC and keeps the kit for the next one, for example when you're done with a customer's PC.

Cleaning a PC removes only what DiagKit put there:
- Registry keys, driver services and temp files a tool left behind.
- The PawnIO driver, but only on a PC where DiagKit's health check installed it. It is optional, and listed with the date it was installed.
- Windows' records of programs that ran from the kit folder: Prefetch, Explorer's launch history and program-name cache, compatibility records, background-activity entries, taskbar/jump-list data and Recent Items. These are matched on the kit's path, under its current or former names and any drive letter, so the same tool run from anywhere else keeps its records.
- The runtime files older DiagKit versions unpacked to `%TEMP%`, and reports DiagKit saved on this PC when the kit was read-only (optional).

Anything that was there before DiagKit is left alone. If the PC owner already has GPU-Z (or Process Explorer, HWiNFO…), its program files, settings and driver are never touched. DiagKit can tell the difference because of three things:
- **Its own record.** Setup records every file and tool folder it installs, the health check records a PawnIO install, and launches record any registry key a tool creates.
- **Snapshots around each launch.** Before a tool starts, DiagKit notes which of its registry keys and driver services already exist. Afterwards it deletes only the new ones, and puts pre-existing keys back exactly as they were.
- **Path matching.** Windows' records are matched on the kit's path, never on a program's name.

Windows' own system databases (Amcache, ShimCache, SRUM and the event logs) also note that programs ran. Uninstall leaves those alone, because editing them could damage Windows.

The record lives in the kit, so other PCs the kit was used on can only be cleaned there. Uninstall names them before it deletes the kit.

## Using it

**Tools** (Ctrl+1). Click a tile to launch the tool.
- Hover a tile (or tab to it) and it expands in place to show the full description, where the tool lives, and a button for its folder. The folder is handy for TestMem5 configs: anta777's Extreme loads by default and Absolut is in `bin`.
- Ctrl+F searches; Enter launches the first match and Esc clears the search.
- Missing tools show where their file goes, and clicking one opens that folder.
- DiagKit explains launch failures: Defender quarantine, Smart App Control, a blocked driver, or a tool that closes immediately.
- When a tool closes, DiagKit undoes what it left behind:
  - registry keys the tool created are removed, and keys that already existed are restored to how they were;
  - driver services the tool installed are removed, along with their driver files.

  If that can't happen yet (DiagKit was closed first, or a driver is still loaded), it is finished at DiagKit's next start or by Uninstall.

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

## Why it's small and fast

- **No runtime to carry.** DiagKit uses the .NET Framework built into Windows, whose WPF is already precompiled. That makes the exe about 4.5 MB instead of a 150 MB self-contained bundle, which matters most on a slow USB stick.
- **One file, nothing unpacked.** The libraries DiagKit uses are embedded in the exe and loaded from memory, so nothing is extracted to `%TEMP%`.
- **Only the builds that run.** Setup leaves out the 32-bit and ARM64 copies many tools ship alongside their 64-bit exe, about 55 MB per kit.
- **The window renders before any disk work.** The tool list is read, tools are searched and icons are extracted on background threads, and the icons are cached per session.
- **No animations on hover.** Tiles expand instantly, and the theme is plain WPF styles with no third-party UI framework.
- **Lazy work.** Pages are created on first visit. The sensor library is only loaded when a health check starts.
- **Collectors never touch the UI thread.** The window stays responsive during a load test because the load threads run at below-normal priority.

## Building

You need the [.NET SDK](https://dotnet.microsoft.com/download) (8 or later) on Windows. It builds the .NET Framework 4.8 programs using reference assemblies from NuGet, so no Visual Studio workload is needed.

```powershell
dotnet build Setup -c Release      # builds DiagKit.exe and Uninstall.exe, embeds them: Release\setup.exe
```

`Release\setup.exe` is the only file to hand out. For development, open `DiagKit.slnx` in Visual Studio or Rider. Visual Studio has to run elevated, because DiagKit and Uninstall require administrator rights.

Repository layout:

```
App\          DiagKit.exe: the launcher and health check
  Views\, ViewModels\, Services\  UI and tool launching (registry/driver cleanup, tool discovery)
  Report\     health-check collectors, rules, HTML/JSON report
  tools.default.json              the built-in tool list, written out as Tools\tools.json
Setup\        setup.exe: tool catalog and sources, download and verification, kit creation
Uninstall\    Uninstall.exe: removal of the kit and of DiagKit's traces on a PC
Shared\       used by all three
  Kit\        the kit ledger, layout names, trace cleanup, PawnIO
  UI\         theme, LED control, view-model helpers
Examples\     a sample health-check report
Release\      build output: setup.exe (not in git)
.build\       intermediate build files (not in git)
```

## Testing status

Tested on Windows 11 24H2 with non-elevated test builds:
- the Tools page, the hover expansion and the health check with GPU sensors read through the embedded LibreHardwareMonitor;
- registry cleanup: a created key was removed, an existing key restored, and a control tool without trace rules left its key in place;
- setup: a new kit, an update, a GUI install with a tool unticked, an upgrade of a kit made by the old build script, and every fallback, by blocking the main hosts in a debug build;
- Uninstall against a kit with planted traces, which removed exactly the kit's entries and kept the user's files and other programs' records. Prefetch decoding was round-trip tested.

Not yet exercised, because they need administrator rights or a PC without PawnIO:
- the PawnIO silent install and uninstall;
- removing driver services;
- deleting Prefetch and background-activity entries.

These use documented Windows APIs and fail safely: anything that can't be removed is reported and retried later.

If DiagKit fails to start, it shows an error box and writes `diagkit-error.log` next to the exe (or in `%TEMP%` when the kit is read-only). If something misbehaves later, `diagkit-error.log` has the stack trace, and each report's `raw\` folder has the tool output the parsers saw.
