# Getting the tools

`build.ps1` downloads every tool after building, so one command gives a complete kit. To update the tools on an existing kit, run `fetch-tools.ps1` on its own:

```powershell
.\build.ps1 -Output E:\DiagKit             # app plus all tools (~120 MB the first time)
.\fetch-tools.ps1 -Kit E:\DiagKit          # later: only downloads tools with a new release
.\fetch-tools.ps1 -Kit E:\DiagKit -Check   # just list what has updates
```

Scoop does not need to be installed. The script reads the [Scoop](https://scoop.sh) community manifests directly, because they carry each tool's current download URL and hash and bots update them within hours of a release.

Every tool has more than one source, tried in order, so one site being down does not stop the kit from updating:

| Tool | Sources, in order | Verified by |
|---|---|---|
| CPU-Z, WizTree, Everything | Scoop Extras manifest, then the Wayback Machine's copy of the same file | SHA-256 from the manifest |
| HWiNFO64 | Scoop Extras manifest (sac.sk), Wayback copy, then the newest portable zip on SourceForge | SHA-256, or the REALiX signature for SourceForge |
| GPU-Z | TechPowerUp's download page (up to six of its mirrors), then the Scoop manifest | SHA-256 from TechPowerUp's page and the TechPowerUp LLC signature |
| CrystalDiskInfo, CrystalDiskMark | Scoop Extras manifest via SourceForge, then SourceForge's master host | SHA-1 from the manifest |
| smartctl | Scoop Main manifest via SourceForge, then the smartmontools GitHub release; `smartctl.exe` is taken out of the installer with 7-Zip | SHA-1 from the manifest |
| Autoruns + autorunsc, Process Explorer, RAMMap | download.sysinternals.com zips, then the single files on live.sysinternals.com | Microsoft Authenticode signature |
| BlueScreenView | Scoop Nirsoft manifest, then nirsoft.net | SHA-256 and the Nir Sofer signature |
| PawnIO setup | GitHub release (API), then the release's direct link | GitHub's SHA-256 and the namazso.eu signature |
| TestMem5 | testmem.tz.ru, then the Wayback Machine | pinned SHA-256 of the two v0.12 builds |
| TestMem5 configs | integralfx/MemTestHelper at a fixed commit, from GitHub, then jsDelivr | pinned SHA-256 |

The Scoop manifests themselves come from raw.githubusercontent.com, with jsDelivr as a mirror.

Behaviour worth knowing:
- **Nothing unverified is installed.** If a hash or signature does not match, that source is skipped. A source with nothing to verify against is never used. If every source fails, the tool is listed as FAILED and nothing is copied.
- **Settings survive updates.** `.ini` and `.cfg` files that already exist are never overwritten, so HWiNFO, CPU-Z, TestMem5 and similar keep your settings.
- **Re-runs skip unchanged tools.** Each tool folder gets a `.diagkit-source` stamp: a version for versioned sources, and an ETag/Last-Modified value for fixed links. `-Force` re-downloads anyway.
- **7-Zip is only needed for smartctl and TestMem5.** If 7-Zip is not installed (Program Files, Scoop, or on PATH), the script unpacks a temporary copy from the official MSI without installing it.
- **A running tool cannot be updated.** If a tool is open, its update fails with "file in use"; close it and run the script again.
- **A new TestMem5 build fails on purpose.** TestMem5 is unsigned and publishes no hashes, so its files are pinned. If the author releases a new build, the script names the file and its hash; check it and add the hash to `fetch-tools.ps1`.

DiagKit finds tools by file name, searching each tool's folder and then all of `tools\` up to three levels deep, so manually added copies work too. Press **Rescan** on the Tools page after adding any.

## TestMem5 configs

TestMem5's built-in test is weak, so the kit adds anta777's configs from the [integralfx DDR4 OC guide](https://github.com/integralfx/MemTestHelper/blob/oc-guide/DDR4%20OC%20Guide.md):

- `bin\MT.cfg` is **Extreme1**, which loads by default.
- `bin\absolut@anta777.cfg` is **Absolut**, a much longer run. Load it with *Load config & exit*, then start TM5 again.

TM5 remembers the loaded config as an absolute path in `bin\Cfg.link`, which breaks when the stick gets a different drive letter. DiagKit repairs that link before each launch, so the chosen config keeps loading.

## Used by the health check

These improve the report; the check still runs without them, with reduced coverage that the report states plainly.

- **smartctl**: full SMART data (reallocated, pending and uncorrectable sectors, NVMe wear and media errors). Without it, only Windows' own healthy/unhealthy flag is available, which misses early failure signs.
- **BlueScreenView**: names the driver behind each crash dump. Without it, crashes are counted from the event log but not attributed.
- **autorunsc64.exe**: the console version of Autoruns, which ships in the same zip. It powers the startup-entry scan.
- **PawnIO setup**: installs the CPU temperature driver; see below.

## PawnIO and CPU temperatures

LibreHardwareMonitor, which DiagKit uses for sensors, reads CPU temperatures through the signed **PawnIO** driver. Its predecessor, WinRing0, is now flagged by Defender and blocked by Memory Integrity. PawnIO has to be **installed** on the PC being checked, which is the one non-portable part of the kit.

There is no Tools tile for it. When PawnIO is missing, the health check shows **Install the CPU temperature driver** (on by default). With it ticked, the check installs PawnIO silently as its first step. PawnIO stays installed and can be removed in Settings > Apps. Untick it on machines where you don't want a driver installed. GPU temperatures still work without it, and the CPU load test falls back to Windows performance counters for clock speeds, so throttling is still visible.

## Adding your own tools

Edit `tools.json` next to `DiagKit.exe`. It is created on first run, comments are allowed, and Rescan reloads it. Fields:

- `folder`: the subfolder of `tools\` to search first.
- `executables`: file name patterns tried in order (`*` and `?` are allowed).
- `arguments`: passed on every launch.
- `registryTraces`: registry keys deleted after the tool exits, but only if they did not exist before launch.
- `registryParents`: the same, but only deleted when left empty.
- `hidden`: `true` for tools used only by the health check (not shown as a tile).
