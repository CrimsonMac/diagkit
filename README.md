# DiagKit

A portable toolkit for diagnosing Windows PCs from a USB stick: a launcher for 19 portable diagnostic tools, and a health check that reads drive health, crash history, hardware errors, temperatures and startup entries and explains what it finds in plain language.

One small exe, the tools next to it, and reports that travel with the stick. When you are done with a PC, Uninstall.exe removes what DiagKit and its tools left on it.

- [Features](#features)
- [Requirements](#requirements)
- [Getting a kit](#getting-a-kit)
- [Using it](#using-it)
- [Removing DiagKit](#removing-diagkit)
- [Building from source](#building-from-source)
- [Compatibility and testing](#compatibility-and-testing)
- [Troubleshooting](#troubleshooting)
- [License](#license)

## Features

- **One-click setup.** setup.exe downloads every tool from its publisher, checks each file against a published hash or signature, and builds or updates a kit on a USB stick or in any folder.
- **Tool launcher.** Searchable tiles for HWiNFO64, CPU-Z, GPU-Z, CrystalDiskInfo, Sysinternals, NirSoft utilities, TestMem5, Prime95 and more. See [TOOLS.md](TOOLS.md) for the full list.
- **Health check.** Drive health, WHEA errors, unexpected shutdowns, bug checks, crash dumps, device problems, battery wear, temperatures and an optional CPU load test. Each finding comes with a plain explanation, what to do about it, and the evidence behind it.
- **Quick actions.** One click checks just the drives, temperatures, crashes, memory, devices, battery or startup programs, or runs a CPU load test; Custom diagnosis runs the checks you tick as one report.
- **Light and dark.** Follows Windows' app mode, or stays dark or light, set in Settings.
- **Shareable reports.** A self-contained HTML file that works on a phone, plus the full JSON and every tool's raw output.
- **Leaves no trace.** Registry keys and drivers a tool creates are removed when it closes, and Uninstall.exe cleans what is left. Anything that was on the PC before DiagKit is never touched.
- **Small and fast.** About 4.5 MB, with no runtime to carry and nothing unpacked to `%TEMP%`.

## Requirements

- 64-bit Windows 10 (1903 or later) or Windows 11.
- .NET Framework 4.8, which is part of those Windows versions, so the PCs you diagnose need nothing extra.
- Administrator rights for DiagKit and Uninstall.exe. Setup does not need them.
- An internet connection for setup. DiagKit itself works offline.

## Getting a kit

Download **setup.exe** from the [Releases](https://github.com/CrimsonMac/diagkit/releases) page, or [build it yourself](#building-from-source), and run it.

setup.exe is not code-signed, so Windows SmartScreen may warn about it the first time: choose **More info**, then **Run anyway**. Each release lists the SHA-256 of setup.exe, so you can check your download with `Get-FileHash setup.exe`.

1. **Pick the location.** USB sticks are listed as one-click choices; **Change…** opens a folder picker that lists every drive with its free space, can make a new folder, marks existing kits and shows where the kit will end up before you confirm. Setup always creates `diagkit.<version>` inside the folder you pick, so picking the top of a drive (`E:\`) gives the kit a folder of its own instead of spreading its files across the stick. Pick a folder that already holds a kit, or the kit itself, and setup updates that kit instead.
2. **Tick the tools.** All of them are ticked for a new kit, about 110 MB in total. The ones the health check needs can't be unticked. On an existing kit, unticking a tool removes it, and setup remembers that choice next time.
3. **Install.** Three downloads run at a time. Every file is checked against its publisher's hash or signature before it goes into the kit, and each tool has fallback sources in case a site is down. See [TOOLS.md](TOOLS.md) for the details.
4. **Open DiagKit or the kit folder** when it finishes. Setup closes once either one opens.

Run setup again whenever you like: it only downloads tools that have a new release. Updating a kit renames the folder to the new version. Tools you added to the kit yourself, and your reports, are kept.

Setup leaves nothing on the PC it runs on, unless you tick **Add DiagKit to the Start menu on this PC**. Windows Search only looks in indexed places such as your user folders and the Start menu, so a kit on another drive or a USB stick doesn't show up when you search for DiagKit; the shortcut fixes that. It is ticked by default for a kit on a fixed drive and unticked for a USB stick. Setup records the shortcut in the kit, keeps it pointing at the kit when an update renames the folder, and removes it if you untick the option later. Uninstall removes it too.

```
setup.exe [/target <folder>] [/tools name,name] [/force] [/quiet]
  /target   where the kit goes: an existing kit, or a folder to create diagkit.<version> in
  /tools    update only these tools and leave the rest of the kit as it is
  /force    download every tool again, even when it is up to date
  /quiet    no window; logs to setup.log next to setup.exe; exit code 0 when every tool installed
```

The kit:

```
diagkit.1.1.1\
  DiagKit.exe           the app (runs as administrator); one file, about 4.5 MB
  Uninstall.exe         cleans this PC and/or removes the kit
  Tools\                one folder per tool, plus tools.json (the tool list, editable)
  Reports\<PC>_<date>\  report.html, report.json, raw\
  settings.json         DiagKit's settings, written once you change one
  .diagkit              hidden record of what DiagKit installed, for Uninstall.exe
```

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
- To add your own tools, edit `Tools\tools.json`; see [TOOLS.md](TOOLS.md#adding-your-own-tools).

**Quick** (Ctrl+2) runs part of the health check when you only want to see one thing:
- **Quick actions**: Drive health (drive health and free space), Temperatures, CPU load test (30 s or 2 min), Crashes and hardware errors (event logs and crash dumps), Memory, Devices, Battery, Startup programs, and System and Windows.
- **Custom diagnosis**: tick any of the health-check steps below, optionally add the load test, and run them together.

Both show their progress on the Health page and save a normal report, marked with what it covered ("Quick check: Drive health"); the verdict then speaks only for the parts that were checked. The system overview is always included so the report names the PC, and the load test brings the temperature step along because it reads temperatures through it.

**Health** (Ctrl+3, F5 from anywhere) runs the full check, with these steps:

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

**Reports** (Ctrl+4) lists saved checks. Each one is a folder containing:
- `report.html`: one self-contained file with no scripts or external assets, which works on a phone and follows light/dark mode. It's the one to send someone.
- `report.json`: the full structured data.
- `raw\`: every tool's original output.

[`Examples/sample-report.html`](Examples/sample-report.html) shows what a report for a troubled PC looks like.

**Settings** (Ctrl+, or the gear at the bottom of the rail):
- **Theme**: System (default; follows Windows' light or dark app mode, and changes with it), Dark or Light. It switches at once.
- **Open on**: the page DiagKit starts on.
- **Open the report in the browser** when a check finishes.
- **Full health check defaults**: the load test length, the startup scan, and whether the PawnIO driver option starts ticked.

Settings are saved to `settings.json` in the kit, so they travel with the stick and nothing is written to the PC. On a read-only kit they last until DiagKit closes. Uninstall removes the file with the rest of the kit.

## Removing DiagKit

**Uninstall.exe**, in the kit or behind the button at the bottom of DiagKit's navigation rail (DiagKit asks before opening it), offers two modes:

- **Remove DiagKit completely.** Deletes the kit and cleans this PC. Files you added to the kit are listed and kept, and the kit folder is only removed once it is empty.
- **Only clean this PC.** Cleans this PC and keeps the kit for the next one, for example when you're done with a customer's PC.

Cleaning a PC removes only what DiagKit put there:
- Registry keys, driver services and temp files a tool left behind.
- The Start menu shortcut, if setup made one on this PC (and it still points at DiagKit).
- The PawnIO driver, but only on a PC where DiagKit's health check installed it. It is optional, and listed with the date it was installed.
- Windows' records of programs that ran from the kit folder: Prefetch, Explorer's launch history and program-name cache, compatibility records, background-activity entries, taskbar/jump-list data and Recent Items. These are matched on the kit's path, under its current or former names and any drive letter, so the same tool run from anywhere else keeps its records.
- The runtime files older DiagKit versions unpacked to `%TEMP%`, and reports DiagKit saved on this PC when the kit was read-only (optional).

Anything that was there before DiagKit is left alone. If the PC owner already has GPU-Z (or Process Explorer, HWiNFO…), its program files, settings and driver are never touched. DiagKit can tell the difference because of three things:
- **Its own record.** Setup records every file and tool folder it installs, the health check records a PawnIO install, and launches record any registry key a tool creates.
- **Snapshots around each launch.** Before a tool starts, DiagKit notes which of its registry keys and driver services already exist. Afterwards it deletes only the new ones, and puts pre-existing keys back exactly as they were.
- **Path matching.** Windows' records are matched on the kit's path, never on a program's name.

Windows' own system databases (Amcache, ShimCache, SRUM and the event logs) also note that programs ran. Uninstall leaves those alone, because editing them could damage Windows.

The record lives in the kit, so other PCs the kit was used on can only be cleaned there. Uninstall names them before it deletes the kit.

## Why it's small and fast

- **No runtime to carry.** DiagKit uses the .NET Framework built into Windows, whose WPF is already precompiled. That makes the exe about 4.5 MB instead of a 150 MB self-contained bundle, which matters most on a slow USB stick.
- **One file, nothing unpacked.** The libraries DiagKit uses are embedded in the exe and loaded from memory, so nothing is extracted to `%TEMP%`.
- **Only the builds that run.** Setup leaves out the 32-bit and ARM64 copies many tools ship alongside their 64-bit exe, about 55 MB per kit.
- **The window renders before any disk work.** The tool list is read, tools are searched and icons are extracted on background threads, and the icons are cached per session.
- **No animations on hover.** Tiles expand instantly, and the theme is plain WPF styles with no third-party UI framework.
- **Lazy work.** Pages are created on first visit. The sensor library is only loaded when a health check starts.
- **Collectors never touch the UI thread.** The window stays responsive during a load test because the load threads run at below-normal priority.

## Building from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) (8 or later) on Windows. It builds the .NET Framework 4.8 programs using reference assemblies from NuGet, so no Visual Studio workload is needed.

```powershell
git clone https://github.com/CrimsonMac/diagkit.git
cd diagkit
dotnet build Setup -c Release      # builds DiagKit.exe and Uninstall.exe, embeds them: Release\setup.exe
```

`Release\setup.exe` is the only file to hand out. The version comes from `Directory.Build.props`.

For development, open `DiagKit.slnx` in Visual Studio or Rider. Visual Studio has to run elevated, because DiagKit and Uninstall require administrator rights.

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

## Compatibility and testing

DiagKit has been run on:
- **Windows 11 24H2**: the Tools page, the health check including sensors, setup and Uninstall.
- **Windows 10**, in a virtual machine: everything runs, but a VM exposes few of the sensors and drive details real hardware has, so some health-check readings were empty. They use the same code paths as on Windows 11.

Also tested with non-elevated test builds:
- registry cleanup: a created key was removed, an existing key restored, and a control tool without trace rules left its key in place;
- setup: a new kit, an update, a GUI install with a tool unticked, an upgrade of a kit made by the old build script, and every fallback, by blocking the main hosts in a debug build;
- Uninstall against a kit with planted traces, which removed exactly the kit's entries and kept the user's files and other programs' records. Prefetch decoding was round-trip tested.
- on Windows 11 24H2, driven through UI Automation: setup's folder picker (browsing, making a new folder, handing it back to setup); switching the theme from dark to light while DiagKit runs, with the choice saved to `settings.json`; and the Quick page, with the System and Windows quick action and a custom diagnosis of Memory and Devices, both producing scoped reports.

Not yet exercised in a controlled test:
- the PawnIO silent install and uninstall;
- removing driver services;
- deleting Prefetch and background-activity entries.

These use documented Windows APIs and fail safely: anything that can't be removed is reported and retried later.

Also not yet tried: the System theme following a change of Windows' app mode while DiagKit is open, and the remaining quick actions; only the two above ran, and without elevation. They run the same steps as the full health check.

## Troubleshooting

- **DiagKit doesn't start.** It shows an error box and writes `diagkit-error.log` next to the exe, or in `%TEMP%` when the kit is read-only.
- **Something misbehaves later.** `diagkit-error.log` has the stack trace, and each report's `raw\` folder has the tool output the parsers saw.
- **A tool won't download.** Setup lists every source it tried. Run it again later, or see [TOOLS.md](TOOLS.md#sources-and-verification) for adding a tool by hand.
- **SmartScreen blocks setup.exe.** It isn't code-signed; choose **More info**, then **Run anyway**, after checking its SHA-256 against the release page.
- **An antivirus flags Uninstall.exe.** It deletes Prefetch and Explorer launch records for the kit's own path, which some heuristics treat as suspicious. The source is all here to check.

When reporting a problem, include `diagkit-error.log` and, for a wrong finding, the report's `raw\` folder.

## License

### Scope

DiagKit is a launcher, a report generator, a setup program and an uninstaller. It does not include, modify, patch or repackage any of the tools it launches:

- **The tools are not in this repository or in setup.exe.** Setup downloads each one, at the user's request, from its publisher or from a mirror of the publisher's unmodified file. Each file is checked against the publisher's hash or signature before it goes into the kit.
- **Tools run unmodified, as separate programs.** DiagKit starts them with ordinary command-line options and reads their output files. Setup leaves out files a 64-bit PC doesn't use (32-bit and ARM64 builds) and adds TestMem5's community configs, and DiagKit keeps TestMem5's config link pointing at the right drive. The programs themselves are never changed.
- **Each tool stays under its publisher's license.** Using a tool through DiagKit is the same as downloading and running it yourself, and its license terms apply in the same way.

DiagKit is meant to operate within the license of every program involved. Some points matter in practice:

- **Commercial use.** Several tools are free for personal use only. HWiNFO64's free version is [non-commercial only](https://www.hwinfo.com/licenses/), and WizTree needs a [supporter licence](https://diskanalyzer.com/eula) for business use. If you diagnose PCs for a living, check each tool's terms and buy the licences that apply, or untick those tools in setup.
- **Sysinternals.** DiagKit passes `-accepteula` to the Sysinternals tools (Autoruns, Process Explorer, Process Monitor, RAMMap, TCPView) so they don't stop on a first-run dialog. By using DiagKit you accept the [Sysinternals Software License Terms](https://learn.microsoft.com/sysinternals/license-terms) on your own behalf.
- **Passing a kit on.** A finished kit contains the downloaded tools. Copying it to someone else is redistribution, which some licenses don't allow. Hand out setup.exe instead, and let it download the tools.

| Tool | Publisher | Terms |
|---|---|---|
| HWiNFO64 | REALiX | [hwinfo.com](https://www.hwinfo.com/licenses/) (free for non-commercial use) |
| CPU-Z | CPUID | [cpuid.com](https://www.cpuid.com/softwares/cpu-z.html) |
| GPU-Z | TechPowerUp | [techpowerup.com](https://www.techpowerup.com/gpuz/) |
| CrystalDiskInfo, CrystalDiskMark | Crystal Dew World | [crystalmark.info](https://crystalmark.info/) |
| WizTree | Antibody Software | [diskanalyzer.com](https://diskanalyzer.com/eula) (business use needs a licence) |
| Everything | voidtools | [voidtools.com](https://www.voidtools.com/) |
| TestMem5 | Serj | [testmem.tz.ru](https://testmem.tz.ru/) |
| TestMem5 configs | anta777, via integralfx/MemTestHelper | [GitHub](https://github.com/integralfx/MemTestHelper) |
| Prime95 | Mersenne Research | [mersenne.org](https://www.mersenne.org/legal/) |
| BlueScreenView, TurnedOnTimesView, AppCrashView, BatteryInfoView, USBDeview | NirSoft | [nirsoft.net](https://www.nirsoft.net/) (freeware) |
| Autoruns, Process Explorer, Process Monitor, RAMMap, TCPView | Microsoft Sysinternals | [Sysinternals license terms](https://learn.microsoft.com/sysinternals/license-terms) |
| smartctl | smartmontools | [GPL-2.0](https://www.smartmontools.org/) |
| PawnIO | namazso | [GPL-2.0-or-later with exceptions](https://github.com/namazso/PawnIO) |

The libraries embedded in DiagKit.exe (LibreHardwareMonitor and others, under MPL-2.0, Apache-2.0 and MIT) are listed with their licenses in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

All product names and trademarks belong to their owners. DiagKit is not affiliated with or endorsed by any of the publishers above.

### DiagKit itself

Copyright (C) 2026 CrimsonMac. DiagKit is free software, licensed under the [GNU General Public License v3.0](LICENSE).

In short: you may use, study, change and share DiagKit, including commercially. Any version you distribute, changed or not, must also be under GPL-3.0, with its source code available. DiagKit comes with no warranty.

The GPL covers DiagKit's own code only. The tools setup downloads keep their own licenses, as described above.
