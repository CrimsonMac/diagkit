using System.Collections.Generic;

namespace DiagKit.Setup;

/// <summary>Where a tool can be downloaded from. Sources of a tool are tried in order until one downloads and verifies.</summary>
public sealed class Source
{
    /// <summary>
    /// scoop        Scoop manifest (Spec = bucket/name): current version, URL and hash. Scoop itself is not needed.
    /// github       latest GitHub release (Repo, Asset regex): version from the tag, SHA-256 from GitHub's asset digest
    /// techpowerup  TechPowerUp's download form (Page, File regex with a "version" group): SHA-256 from the page
    /// sourceforge  newest file matching Pattern in a SourceForge folder (Project, Path), from its RSS feed; no hash
    /// files        several single files from one base URL (Base, Files); needs a Signer
    /// url          fixed link (Url); ETag/Last-Modified tell whether it changed
    /// </summary>
    public string Kind { get; set; } = "";

    public string Spec { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Asset { get; set; } = "";
    public string Page { get; set; } = "";
    public string File { get; set; } = "";
    public string Project { get; set; } = "";
    public string Path { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string Base { get; set; } = "";
    public string[] Files { get; set; } = new string[0];
    public string Url { get; set; } = "";

    /// <summary>Other hosts serving the same path. Only used for hash-verified downloads, so any copy is as good as the original.</summary>
    public string[] Mirrors { get; set; } = new string[0];

    /// <summary>Other URLs for the same file; {version} and {version_} (dots as underscores) are filled in.</summary>
    public string[] AltUrls { get; set; } = new string[0];
}

/// <summary>An extra file from a GitHub repository at a fixed commit, checked against a pinned hash.</summary>
public sealed class Extra
{
    public string Path { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Commit { get; set; } = "";
    public string File { get; set; } = "";
}

public sealed class ToolSpec
{
    public string Name { get; set; } = "";

    /// <summary>Folder under Tools\; matches tools.json so DiagKit finds the tool.</summary>
    public string Folder { get; set; } = "";

    public string Category { get; set; } = "";

    /// <summary>Approximate size in the kit, for the setup screen.</summary>
    public double SizeMb { get; set; }

    /// <summary>Needed by the health check rather than launched as a tile; always installed.</summary>
    public bool Required { get; set; }

    /// <summary>Authenticode signer every .exe/.dll must carry (certificate subject name, exact match).</summary>
    public string? Signer { get; set; }

    /// <summary>Fixed file name for single-file tools, so an update replaces the old exe instead of adding a second one.</summary>
    public string? SaveAs { get; set; }

    /// <summary>Take only this file out of a bigger package (smartctl.exe from the smartmontools installer).</summary>
    public string? Pick { get; set; }

    /// <summary>Pinned SHA-256 per file, for tools with neither a published hash nor a signature. No other code may come along.</summary>
    public Dictionary<string, string[]>? FileHashes { get; set; }

    /// <summary>
    /// Files left out of the kit (wildcards allowed): 32-bit and ARM64 builds DiagKit never starts, since it only runs
    /// on 64-bit Windows. They are removed from existing kits too.
    /// </summary>
    public string[] Skip { get; set; } = new string[0];

    public List<Extra> Extras { get; set; } = new();

    /// <summary>What to tell the user when every source failed.</summary>
    public string? Manual { get; set; }

    public List<Source> Sources { get; set; } = new();
}

/// <summary>The tools setup can put in a kit. Folder names match App\tools.default.json.</summary>
public static class Catalog
{
    private const string Sysinternals = "Microsoft Corporation";
    private const string NirSoft = "Nir Sofer";
    private const string MemTestHelperCommit = "da422e50ec2170b578301fe9cc77f101143c38a7";

    private static Source Scoop(string spec) => new() { Kind = "scoop", Spec = spec };

    private static Source Url(string url) => new() { Kind = "url", Url = url };

    /// <summary>Sysinternals: the zip, then the same signed files one by one from Sysinternals Live.</summary>
    private static List<Source> SysinternalsSources(string zip, params string[] liveFiles) => new()
    {
        Url("https://download.sysinternals.com/files/" + zip),
        new Source { Kind = "files", Base = "https://live.sysinternals.com/", Files = liveFiles },
    };

    /// <summary>NirSoft: the Scoop manifest (with hash), then nirsoft.net directly (signature only).</summary>
    private static List<Source> NirSoftSources(string scoopName, string zipUrl) => new()
    {
        Scoop("Nirsoft/" + scoopName),
        Url(zipUrl),
    };

    public static List<ToolSpec> All { get; } = new()
    {
        // ---- Hardware
        new ToolSpec
        {
            // HWiNFO's own site is behind a browser challenge; its SourceForge folder lags a few versions but is official.
            Name = "HWiNFO64", Folder = "HWiNFO64", Category = "Hardware", SizeMb = 11, Signer = "REALiX, s.r.o.",
            Skip = new[] { "HWiNFO32.exe", "HWiNFO_ARM64.exe" },
            Sources =
            {
                Scoop("Extras/hwinfo"),
                new Source { Kind = "sourceforge", Project = "hwinfo", Path = "Windows_Portable", Pattern = @"^hwi_\d+\.zip$" },
            },
        },
        new ToolSpec
        {
            Name = "CPU-Z", Folder = "CPU-Z", Category = "Hardware", SizeMb = 7.5,
            Skip = new[] { "cpuz_x32.exe" },
            Sources = { Scoop("Extras/cpu-z") },
        },
        new ToolSpec
        {
            // TechPowerUp only serves GPU-Z through its download form (direct /files/ links return 404), so the form is
            // the main source. The Scoop manifest stays as a second opinion with its own hash, in case the page changes.
            Name = "GPU-Z", Folder = "GPU-Z", Category = "Hardware", SizeMb = 12, Signer = "TechPowerUp LLC", SaveAs = "GPU-Z.exe",
            Sources =
            {
                new Source { Kind = "techpowerup", Page = "techpowerup-gpu-z", File = @"^GPU-Z\.(?<version>[\d.]+)\.exe$" },
                new Source { Kind = "scoop", Spec = "Extras/gpu-z", Mirrors = new[] { "https://us1-dl.techpowerup.com", "https://us2-dl.techpowerup.com" } },
            },
            Manual = @"Download it once from https://www.techpowerup.com/gpuz/ into Tools\GPU-Z; the next run checks it against the same hash and marks it up to date.",
        },
        new ToolSpec
        {
            // Unlike most NirSoft tools this one is not code-signed, so only the Scoop manifest's hash can vouch for it.
            Name = "BatteryInfoView", Folder = "BatteryInfoView", Category = "Hardware", SizeMb = 0.2,
            Sources = { Scoop("Nirsoft/batteryinfoview") },
        },

        // ---- Storage
        new ToolSpec
        {
            Name = "CrystalDiskInfo", Folder = "CrystalDiskInfo", Category = "Storage", SizeMb = 13.5,
            Skip = new[] { "DiskInfo32.exe", "DiskInfoA64.exe" },
            Sources = { Scoop("Extras/crystaldiskinfo") },
        },
        new ToolSpec
        {
            Name = "CrystalDiskMark", Folder = "CrystalDiskMark", Category = "Storage", SizeMb = 4.5,
            Skip = new[] { "DiskMark32.exe", "DiskMarkA64.exe", @"CdmResource\DiskSpd\DiskSpd32.exe", @"CdmResource\DiskSpd\DiskSpdA64.exe" },
            Sources = { Scoop("Extras/crystaldiskmark") },
        },
        new ToolSpec
        {
            Name = "WizTree", Folder = "WizTree", Category = "Storage", SizeMb = 12,
            Skip = new[] { "WizTree.exe" },
            Sources = { Scoop("Extras/wiztree") },
        },
        new ToolSpec
        {
            Name = "Everything", Folder = "Everything", Category = "Storage", SizeMb = 3.2,
            Sources = { Scoop("Extras/everything") },
        },

        // ---- Memory and stability
        new ToolSpec
        {
            // TestMem5 is unsigned and its author publishes no hashes, so its two binaries are pinned to the known v0.12
            // builds (testmem.tz.ru has shipped two TM5.exe builds). Any other code in the archive is refused.
            // The Wayback Machine copy is the fallback; the pinned hashes make an archived copy as safe as the original.
            Name = "TestMem5", Folder = "TestMem5", Category = "Memory and stability", SizeMb = 0.1,
            FileHashes = new Dictionary<string, string[]>
            {
                ["TM5.exe"] = new[] { "B7ED4B39DE012E88EC9AD7F21B0AC68A4E863FE078B5C54857A16D37D480CC21", "ED8056A6B3CDB301D1E1571B9E832AAEF4131249830EAA8860391D5380FA16E6" },
                [@"bin\MT0.dll"] = new[] { "3EE244962C2683022C3BCC78D1A869E755516BBF4D2C1123A8A55E47655731EF" },
            },
            Sources =
            {
                Url("https://testmem.tz.ru/tm5.rar"),
                Url("https://web.archive.org/web/2025id_/https://testmem.tz.ru/tm5.rar"),
            },
            // TM5's built-in default test is weak. anta777's configs (from the integralfx DDR4 OC guide, pinned to a commit)
            // give it real coverage: Extreme1 becomes the default bin\MT.cfg, Absolut is there to load for a longer run.
            Extras =
            {
                new Extra { Path = @"bin\MT.cfg", Hash = "sha256:E7B17592CBAEA3B8A4F86E015FDEE7062F5409CCE64F58DFD4CDA392F0B60BB5", Repo = "integralfx/MemTestHelper", Commit = MemTestHelperCommit, File = "TM5-Configs/extreme@anta777.cfg" },
                new Extra { Path = @"bin\extreme@anta777.cfg", Hash = "sha256:E7B17592CBAEA3B8A4F86E015FDEE7062F5409CCE64F58DFD4CDA392F0B60BB5", Repo = "integralfx/MemTestHelper", Commit = MemTestHelperCommit, File = "TM5-Configs/extreme@anta777.cfg" },
                new Extra { Path = @"bin\absolut@anta777.cfg", Hash = "sha256:967FCD496753C0013D7E682A2F43980EEB87C8B99A023961D35CEC9F7A2310C3", Repo = "integralfx/MemTestHelper", Commit = MemTestHelperCommit, File = "TM5-Configs/absolutnew.cfg" },
            },
        },
        new ToolSpec
        {
            // Unsigned; the Scoop manifest carries the official mersenne.ca link and its SHA-256.
            Name = "Prime95", Folder = "Prime95", Category = "Memory and stability", SizeMb = 24,
            Sources = { Scoop("Extras/prime95") },
        },

        // ---- Crashes and startup
        new ToolSpec
        {
            Name = "BlueScreenView", Folder = "BlueScreenView", Category = "Crashes and startup", SizeMb = 0.2, Signer = NirSoft, Required = true,
            Sources = NirSoftSources("bluescreenview", "https://www.nirsoft.net/utils/bluescreenview-x64.zip"),
        },
        new ToolSpec
        {
            Name = "TurnedOnTimesView", Folder = "TurnedOnTimesView", Category = "Crashes and startup", SizeMb = 0.2, Signer = NirSoft,
            Sources = NirSoftSources("turnedontimesview", "https://www.nirsoft.net/utils/turnedontimesview.zip"),
        },
        new ToolSpec
        {
            Name = "AppCrashView", Folder = "AppCrashView", Category = "Crashes and startup", SizeMb = 0.1, Signer = NirSoft,
            Sources = NirSoftSources("appcrashview", "https://www.nirsoft.net/utils/appcrashview.zip"),
        },
        new ToolSpec
        {
            // The zip also carries autorunsc64.exe, which the health check uses for its startup scan.
            Name = "Autoruns", Folder = "Autoruns", Category = "Crashes and startup", SizeMb = 3.2, Signer = Sysinternals, Required = true,
            Skip = new[] { "Autoruns.exe", "Autoruns64a.exe", "autorunsc.exe", "autorunsc64a.exe" },
            Sources = SysinternalsSources("Autoruns.zip", "Autoruns64.exe", "autorunsc64.exe"),
        },

        // ---- Processes and memory
        new ToolSpec
        {
            Name = "Process Explorer", Folder = "ProcessExplorer", Category = "Processes and memory", SizeMb = 2.4, Signer = Sysinternals,
            Skip = new[] { "procexp.exe", "procexp64a.exe" },
            Sources = SysinternalsSources("ProcessExplorer.zip", "procexp64.exe"),
        },
        new ToolSpec
        {
            Name = "Process Monitor", Folder = "ProcessMonitor", Category = "Processes and memory", SizeMb = 2.2, Signer = Sysinternals,
            Skip = new[] { "Procmon.exe", "Procmon64a.exe" },
            Sources = SysinternalsSources("ProcessMonitor.zip", "Procmon64.exe"),
        },
        new ToolSpec
        {
            Name = "RAMMap", Folder = "RAMMap", Category = "Processes and memory", SizeMb = 0.5, Signer = Sysinternals,
            Skip = new[] { "RAMMap.exe", "RAMMap64a.exe" },
            Sources = SysinternalsSources("RAMMap.zip", "RAMMap64.exe"),
        },

        // ---- Devices and network
        new ToolSpec
        {
            // Not code-signed (unlike most NirSoft tools), so only the Scoop manifest's hash can vouch for it.
            Name = "USBDeview", Folder = "USBDeview", Category = "Devices and network", SizeMb = 0.3,
            Sources = { Scoop("Nirsoft/usbdeview") },
        },
        new ToolSpec
        {
            Name = "TCPView", Folder = "TCPView", Category = "Devices and network", SizeMb = 1, Signer = Sysinternals,
            Skip = new[] { "tcpview.exe", "tcpview64a.exe", "tcpvcon*.exe" },
            Sources = SysinternalsSources("TCPView.zip", "tcpview64.exe"),
        },

        // ---- Health check components
        new ToolSpec
        {
            // smartmontools publishes the same installer on GitHub releases, so the manifest's hash covers both.
            Name = "smartctl", Folder = "smartmontools", Category = "Health check components", SizeMb = 1.1, Pick = "smartctl.exe", Required = true,
            Sources =
            {
                new Source
                {
                    Kind = "scoop", Spec = "Main/smartmontools",
                    AltUrls = new[] { "https://github.com/smartmontools/smartmontools/releases/download/RELEASE_{version_}/smartmontools-{version}.win32-setup.exe" },
                },
            },
        },
        new ToolSpec
        {
            Name = "PawnIO setup", Folder = "PawnIO", Category = "Health check components", SizeMb = 3.3, Signer = "namazso.eu", Required = true,
            Sources =
            {
                new Source { Kind = "github", Repo = "namazso/PawnIO.Setup", Asset = @"^PawnIO_setup\.exe$" },
                // The API is rate-limited (60 requests/hour without a token); the plain "latest" link is not.
                Url("https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe"),
            },
        },
    };
}
