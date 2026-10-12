# Getting the tools

Run `fetch-tools.ps1` once and it downloads everything except TestMem5:

```powershell
.\fetch-tools.ps1 -Kit E:\DiagKit     # first run: downloads all tools (~120 MB)
.\fetch-tools.ps1 -Kit E:\DiagKit     # later: only downloads tools with a new release
.\fetch-tools.ps1 -Kit E:\DiagKit -Check    # just list what has updates
```

Scoop does not need to be installed. The script reads the [Scoop](https://scoop.sh) community manifests directly, because they carry each tool's current download URL and hash and bots update them within hours of a release.

| Tool | Source | Verified by |
|---|---|---|
| HWiNFO64, CPU-Z, GPU-Z, WizTree, Everything | Scoop Extras manifest | SHA-256 from the manifest |
| CrystalDiskInfo, CrystalDiskMark | Scoop Extras manifest (SourceForge) | SHA-1 from the manifest |
| smartctl | Scoop Main manifest; `smartctl.exe` is taken out of the smartmontools installer with 7-Zip | SHA-1 from the manifest |
| Autoruns + autorunsc, Process Explorer, RAMMap | download.sysinternals.com | Microsoft Authenticode signature |
| BlueScreenView | nirsoft.net (stable x64 link) | HTTPS only |
| PawnIO setup | GitHub latest release | HTTPS only |
| **TestMem5** | **manual**: copy `TM5.exe` and its `bin` folder into `tools\TestMem5` | n/a |

Behaviour worth knowing:
- **A failed check means nothing is installed.** If a hash or signature does not match, the tool is skipped and listed as FAILED.
- **Settings survive updates.** `.ini` and `.cfg` files that already exist are never overwritten, so HWiNFO, CPU-Z and similar keep your settings.
- **Re-runs skip unchanged tools.** Each tool folder gets a `.diagkit-source` stamp: a version for manifest sources, and an ETag/Last-Modified value for fixed links. `-Force` re-downloads anyway.
- **7-Zip is only needed for smartctl.** If 7-Zip is not installed (Program Files, Scoop, or on PATH), the script unpacks a temporary copy from the official MSI without installing it.
- **A running tool cannot be updated.** If a tool is open, its update fails with "file in use"; close it and run the script again.
- **Updates depend on the manifest.** If a vendor changes their site, the Scoop manifest is usually fixed within a day; until then that tool shows FAILED and the rest still update.

DiagKit finds tools by file name, searching each tool's folder and then all of `tools\` up to three levels deep, so manually added copies work too. Press **Rescan** on the Tools page after adding any.

## Used by the health check

These improve the report; the check still runs without them, with reduced coverage that the report states plainly.

- **smartctl**: full SMART data (reallocated, pending and uncorrectable sectors, NVMe wear and media errors). Without it, only Windows' own healthy/unhealthy flag is available, which misses early failure signs.
- **BlueScreenView**: names the driver behind each crash dump. Without it, crashes are counted from the event log but not attributed.
- **autorunsc64.exe**: the console version of Autoruns, which ships in the same zip. It powers the startup-entry scan.

## PawnIO and CPU temperatures

LibreHardwareMonitor, which DiagKit uses for sensors, reads CPU temperatures through the signed **PawnIO** driver. Its predecessor, WinRing0, is now flagged by Defender and blocked by Memory Integrity. PawnIO has to be **installed** on the PC being checked, which is the one non-portable part of the kit.

Run *PawnIO driver setup* from the Tools page only on machines where you want CPU temperatures and are fine installing a driver. Without it, GPU temperatures still work, and the CPU load test falls back to Windows performance counters for clock speeds, so throttling is still visible.

## Adding your own tools

Edit `tools.json` next to `DiagKit.exe`. It is created on first run, comments are allowed, and Rescan reloads it. Fields:

- `folder`: the subfolder of `tools\` to search first.
- `executables`: file name patterns tried in order (`*` and `?` are allowed).
- `arguments`: passed on every launch.
- `registryTraces`: registry keys deleted after the tool exits, but only if they did not exist before launch.
- `registryParents`: the same, but only deleted when left empty.
- `hidden`: `true` for tools used only by the health check (not shown as a tile).
