# The tools

setup.exe downloads and updates the tools; see the [README](README.md#getting-a-kit) for how to run it. This page covers what is in a kit, where each tool comes from, and how each download is checked.

| Tool | What it's for | Size |
|---|---|---|
| HWiNFO64 | full hardware inventory, live sensors, WHEA error counts | 11 MB |
| CPU-Z | CPU, motherboard and memory details, SPD per stick | 7.5 MB |
| GPU-Z | graphics card identity, BIOS and sensors; spots fake cards | 12 MB |
| BatteryInfoView | laptop battery wear and charge cycles | 0.2 MB |
| CrystalDiskInfo | SMART health for every drive | 13.5 MB |
| CrystalDiskMark | drive speed benchmark | 4.5 MB |
| WizTree | what is filling a drive | 12 MB |
| Everything | instant file name search | 3.2 MB |
| TestMem5 | RAM stress test, with anta777's configs | 0.1 MB |
| Prime95 | CPU and memory torture test | 24 MB |
| BlueScreenView | which driver each crash dump points at | 0.2 MB |
| TurnedOnTimesView | timeline of startups, shutdowns and sudden power losses | 0.2 MB |
| AppCrashView | program crashes Windows Error Reporting recorded | 0.1 MB |
| Autoruns (+ autorunsc) | everything that starts with Windows | 3.2 MB |
| Process Explorer | process tree, handles, DLLs | 2.4 MB |
| Process Monitor | live file, registry and process activity | 2.2 MB |
| RAMMap | where physical memory went | 0.5 MB |
| USBDeview | every USB device the PC has seen | 0.3 MB |
| TCPView | open network connections per program | 1 MB |
| smartctl | full SMART data for the health check | 1.1 MB |
| PawnIO setup | the CPU temperature driver the health check can install | 3.3 MB |

Every tool is portable and small. Larger suites were left out on purpose: OCCT, AIDA64, FurMark (35 MB) and Wireshark are either not free for this use, not portable, or too big for what they add. Many tools ship 32-bit and ARM64 builds next to their 64-bit exe. DiagKit only runs on 64-bit Windows, so setup leaves those out (about 55 MB) and removes them from kits made by older versions.

## Sources and verification

Each tool has more than one source, tried in order, so one site being down does not stop the kit from updating:

| Tool | Sources, in order | Verified by |
|---|---|---|
| CPU-Z, WizTree, Everything | Scoop Extras manifest, then the Wayback Machine's copy of the same file | SHA-256 from the manifest |
| HWiNFO64 | Scoop Extras manifest (sac.sk), Wayback copy, then the newest portable zip on SourceForge | SHA-256, or the REALiX signature for SourceForge |
| GPU-Z | TechPowerUp's download page (up to six of its mirrors), then the Scoop manifest | SHA-256 from TechPowerUp's page and the TechPowerUp LLC signature |
| CrystalDiskInfo, CrystalDiskMark | Scoop Extras manifest via SourceForge, then SourceForge's master host | SHA-1 from the manifest |
| Prime95 | Scoop Extras manifest (mersenne.ca), mersenne.org's copy, Wayback copy | SHA-256 from the manifest |
| smartctl | Scoop Main manifest via SourceForge, then the smartmontools GitHub release; `smartctl.exe` is taken out of the installer with 7-Zip | SHA-1 from the manifest |
| Autoruns, Process Explorer, Process Monitor, RAMMap, TCPView | download.sysinternals.com zips, then the single files on live.sysinternals.com | Microsoft Authenticode signature |
| BlueScreenView, TurnedOnTimesView, AppCrashView | Scoop Nirsoft manifest, then nirsoft.net | SHA-256 and the Nir Sofer signature |
| BatteryInfoView, USBDeview | Scoop Nirsoft manifest, then the Wayback copy | SHA-256 from the manifest (NirSoft does not sign these two) |
| PawnIO setup | GitHub release (API), then the release's direct link | GitHub's SHA-256 and the namazso.eu signature |
| TestMem5 | testmem.tz.ru, then the Wayback Machine | pinned SHA-256 of the two v0.12 builds |
| TestMem5 configs | integralfx/MemTestHelper at a fixed commit, from GitHub, then jsDelivr | pinned SHA-256 |

The Scoop manifests are read straight from raw.githubusercontent.com, with jsDelivr as a mirror; Scoop itself is not needed. They carry each tool's current download link and hash, and bots update them within hours of a release.

Behaviour worth knowing:
- **Nothing unverified is installed.** If a hash or signature doesn't match, that source is skipped, and a source with nothing to verify against is never used. If every source fails, the tool is reported as failed and nothing is copied. Signatures are checked with WinVerifyTrust and must chain to a trusted root and name the expected publisher exactly.
- **Settings survive updates.** `.ini` and `.cfg` files that already exist are never overwritten, so HWiNFO, CPU-Z, TestMem5 and the others keep your settings.
- **Re-runs skip unchanged tools.** The ledger records which release of each tool is installed: a version, or an ETag/Last-Modified value for fixed links.
- **7-Zip is only needed for smartctl and TestMem5.** An installed copy is used if there is one; otherwise setup unpacks a temporary copy from the official MSI into its working folder. Nothing is installed, and the folder is deleted afterwards.
- **A running tool cannot be updated.** Setup refuses to touch a kit while DiagKit or one of its tools is running from it.
- **A new TestMem5 build fails on purpose.** TestMem5 is unsigned and its author publishes no hashes, so its files are pinned. If the author releases a new build, setup names the file and its hash; check it and add the hash in `Setup\Catalog.cs`.
- **GPU-Z has a single real host.** Both of its sources are TechPowerUp. If TechPowerUp is unreachable, download GPU-Z by hand into `Tools\GPU-Z`, and the next setup run accepts it once it matches the published hash.

DiagKit finds tools by file name, searching each tool's folder and then all of `Tools\` up to three levels deep, so manually added copies work too. Press **Rescan** on the Tools page after adding any. Setup and Uninstall leave such copies alone.

## TestMem5 configs

TestMem5's built-in test is weak, so the kit adds anta777's configs from the [integralfx DDR4 OC guide](https://github.com/integralfx/MemTestHelper/blob/oc-guide/DDR4%20OC%20Guide.md):

- `bin\MT.cfg` is **Extreme1**, which loads by default.
- `bin\absolut@anta777.cfg` is **Absolut**, a much longer run. Load it with *Load config & exit*, then start TM5 again.

TM5 remembers the loaded config as an absolute path in `bin\Cfg.link`, which breaks when the stick gets a different drive letter. DiagKit repairs that link before each launch, so the chosen config keeps loading.

## Used by the health check

These improve the report and are always part of the kit. The check still runs without them, with reduced coverage that the report states plainly.

- **smartctl**: full SMART data (reallocated, pending and uncorrectable sectors, NVMe wear and media errors). Without it, only Windows' own healthy/unhealthy flag is available, which misses early failure signs.
- **BlueScreenView**: names the driver behind each crash dump. Without it, crashes are counted from the event log but not attributed.
- **autorunsc64.exe**: the console version of Autoruns, which ships in the same zip. It powers the startup-entry scan.
- **PawnIO setup**: installs the CPU temperature driver; see below.

## PawnIO and CPU temperatures

LibreHardwareMonitor, which DiagKit uses for sensors, reads CPU temperatures through the signed **PawnIO** driver. Its predecessor, WinRing0, is now flagged by Defender and blocked by Memory Integrity. PawnIO has to be **installed** on the PC being checked, which is the one non-portable part of the kit.

When PawnIO is missing, the health check shows **Install the CPU temperature driver** (on by default). With it ticked, the check installs PawnIO silently as its first step and records that in the kit, so **Uninstall.exe** can remove it from that PC again. A PawnIO that was already installed (by FanControl or LibreHardwareMonitor, for example) is never removed. Untick the option on machines where you don't want a driver installed. GPU temperatures still work without it, and the CPU load test falls back to Windows performance counters for clock speeds, so throttling is still visible.

## Adding your own tools

Edit `Tools\tools.json`. It is created on first run, comments are allowed, and Rescan reloads it. Fields:

- `folder`: the subfolder of `Tools\` to search first.
- `executables`: file name patterns tried in order (`*` and `?` are allowed).
- `arguments`: passed on every launch.
- `registryTraces`: registry keys the tool writes. After it exits they are deleted if the tool created them, or restored to their earlier state if they already existed.
- `registryParents`: the same, but only deleted when left empty.
- `services`: driver services the tool installs (`*` allowed). After it exits, those that the run created are stopped and deleted, along with their driver file.
- `files`: files the tool leaves outside its folder, such as `%TEMP%\x*.sys`. After it exits, those created by that run are deleted.
- `hidden`: `true` for tools used only by the health check (not shown as a tile).
