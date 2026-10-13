<#
.SYNOPSIS
    Downloads or updates the portable tools in a DiagKit folder.

.DESCRIPTION
    Every tool has a list of sources that are tried in order, so one site being down does not stop the kit
    from updating. Most tools have versioned download URLs, so instead of hard-coding them this script reads
    the community-maintained Scoop manifests (github.com/ScoopInstaller), which carry the current URL and a
    hash for each release. Scoop itself is NOT needed or installed. Every download is verified before
    anything is copied into the kit, and a source that cannot be verified is skipped:
      - against a hash published by the source (Scoop manifest, GitHub release digest)
      - and/or against the publisher's Authenticode signature (Sysinternals, NirSoft, PawnIO)
    Each tool folder gets a .diagkit-source stamp, so re-running only downloads what changed.
    Existing .ini/.cfg files are never overwritten, so tool settings survive updates.

.EXAMPLE
    .\fetch-tools.ps1                          # fetch/update everything into .\dist\DiagKit
    .\fetch-tools.ps1 -Kit E:\DiagKit          # update the kit on the stick
    .\fetch-tools.ps1 -Check                   # only report what has updates
    .\fetch-tools.ps1 -Only GPU-Z,HWiNFO64     # just these
    .\fetch-tools.ps1 -Force                   # re-download even if up to date
#>
param(
    [string]$Kit = (Join-Path $PSScriptRoot 'dist\DiagKit'),
    [string[]]$Only,
    [switch]$Check,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
# Accept "-Only A,B" whether PowerShell passed a list or one comma-joined string (pwsh -File does the latter).
if ($Only) { $Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
# The progress bar makes Invoke-WebRequest many times slower in Windows PowerShell 5.1.
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
# A non-browser user agent makes SourceForge send the file instead of its "your download will start" page.
$UserAgent = 'DiagKit-fetch/1.1 (PowerShell)'
$KeepExtensions = @('.ini', '.cfg')
$StampName = '.diagkit-source'

# Folder names match tools.json, so DiagKit finds everything on the next Rescan.
# Sources are tried in order until one downloads and verifies. Kinds:
#   scoop   Scoop manifest (bucket/name): current version, URL and hash
#   github  latest GitHub release: version from the tag, SHA-256 from GitHub's asset digest
#   techpowerup  TechPowerUp's download form: version and SHA-256 from the download page, links from its mirror list
#   url     fixed link; ETag/Last-Modified tell whether it changed
# SaveAs: fixed file name for single-file tools, so an update replaces the old exe instead of adding a second one.
# FileHashes: pinned SHA-256 per file, for tools with neither a published hash nor a signature.
# Extras: extra files from a GitHub repo at a fixed commit (raw.githubusercontent.com, then jsDelivr), hash-checked.
# A download is only installed if it can be verified: by the source's hash, the tool's Signer, or both.
# Mirrors: alternative hosts serving the same path; safe because the hash is checked.
# Signer: the Authenticode signer every .exe/.dll must carry (certificate subject name, exact match).
$Tools = @(
    @{ Name = 'HWiNFO64';         Folder = 'HWiNFO64';        Sources = @(@{ Kind = 'scoop'; Spec = 'Extras/hwinfo' }) }
    @{ Name = 'CPU-Z';            Folder = 'CPU-Z';           Sources = @(@{ Kind = 'scoop'; Spec = 'Extras/cpu-z' }) }
    # TechPowerUp only serves GPU-Z through its download form (direct /files/ links return 404), so the form is
    # the main source. The Scoop manifest stays as a second opinion with its own hash, in case the page changes.
    @{ Name = 'GPU-Z';            Folder = 'GPU-Z';           Signer = 'TechPowerUp LLC'; SaveAs = 'GPU-Z.exe'
       Sources = @(
           @{ Kind = 'techpowerup'; Page = 'techpowerup-gpu-z'; File = '^GPU-Z\.(?<version>[\d.]+)\.exe$' }
           @{ Kind = 'scoop'; Spec = 'Extras/gpu-z'; Mirrors = @('https://us1-dl.techpowerup.com', 'https://us2-dl.techpowerup.com') })
       Manual = 'Download it once from https://www.techpowerup.com/gpuz/ into tools\GPU-Z; the next run checks it against the same hash and marks it up to date.' }
    @{ Name = 'CrystalDiskInfo';  Folder = 'CrystalDiskInfo'; Sources = @(@{ Kind = 'scoop'; Spec = 'Extras/crystaldiskinfo' }) }
    @{ Name = 'CrystalDiskMark';  Folder = 'CrystalDiskMark'; Sources = @(@{ Kind = 'scoop'; Spec = 'Extras/crystaldiskmark' }) }
    @{ Name = 'WizTree';          Folder = 'WizTree';         Sources = @(@{ Kind = 'scoop'; Spec = 'Extras/wiztree' }) }
    @{ Name = 'Everything';       Folder = 'Everything';      Sources = @(@{ Kind = 'scoop'; Spec = 'Extras/everything' }) }
    @{ Name = 'smartctl';         Folder = 'smartmontools';   Pick = 'smartctl.exe'; Sources = @(@{ Kind = 'scoop'; Spec = 'Main/smartmontools' }) }
    @{ Name = 'Autoruns';         Folder = 'Autoruns';        Signer = 'Microsoft Corporation'; Sources = @(@{ Kind = 'url'; Url = 'https://download.sysinternals.com/files/Autoruns.zip' }) }
    @{ Name = 'Process Explorer'; Folder = 'ProcessExplorer'; Signer = 'Microsoft Corporation'; Sources = @(@{ Kind = 'url'; Url = 'https://download.sysinternals.com/files/ProcessExplorer.zip' }) }
    @{ Name = 'RAMMap';           Folder = 'RAMMap';          Signer = 'Microsoft Corporation'; Sources = @(@{ Kind = 'url'; Url = 'https://download.sysinternals.com/files/RAMMap.zip' }) }
    # TestMem5 is unsigned and its author publishes no hashes, so its two binaries are pinned to the known v0.12
    # builds (testmem.tz.ru has shipped two TM5.exe builds). Any other code in the archive is refused.
    # The Wayback Machine copy is the fallback; the pinned hashes make an archived copy as safe as the original.
    @{ Name = 'TestMem5';         Folder = 'TestMem5'
       FileHashes = @{
           'TM5.exe'     = @('B7ED4B39DE012E88EC9AD7F21B0AC68A4E863FE078B5C54857A16D37D480CC21', 'ED8056A6B3CDB301D1E1571B9E832AAEF4131249830EAA8860391D5380FA16E6')
           'bin\MT0.dll' = @('3EE244962C2683022C3BCC78D1A869E755516BBF4D2C1123A8A55E47655731EF')
       }
       Sources = @(
           @{ Kind = 'url'; Url = 'https://testmem.tz.ru/tm5.rar' }
           @{ Kind = 'url'; Url = 'https://web.archive.org/web/2025id_/https://testmem.tz.ru/tm5.rar' })
       # TM5's built-in default test is weak. anta777's configs (from the integralfx DDR4 OC guide, pinned to a commit)
       # give it real coverage: Extreme1 becomes the default bin\MT.cfg, Absolut is there to load for a longer run.
       Extras = @(
           @{ Path = 'bin\MT.cfg';                 Hash = 'sha256:E7B17592CBAEA3B8A4F86E015FDEE7062F5409CCE64F58DFD4CDA392F0B60BB5'; Repo = 'integralfx/MemTestHelper'; Commit = 'da422e50ec2170b578301fe9cc77f101143c38a7'; File = 'TM5-Configs/extreme@anta777.cfg' }
           @{ Path = 'bin\extreme@anta777.cfg';    Hash = 'sha256:E7B17592CBAEA3B8A4F86E015FDEE7062F5409CCE64F58DFD4CDA392F0B60BB5'; Repo = 'integralfx/MemTestHelper'; Commit = 'da422e50ec2170b578301fe9cc77f101143c38a7'; File = 'TM5-Configs/extreme@anta777.cfg' }
           @{ Path = 'bin\absolut@anta777.cfg';    Hash = 'sha256:967FCD496753C0013D7E682A2F43980EEB87C8B99A023961D35CEC9F7A2310C3'; Repo = 'integralfx/MemTestHelper'; Commit = 'da422e50ec2170b578301fe9cc77f101143c38a7'; File = 'TM5-Configs/absolutnew.cfg' }
       ) }
    @{ Name = 'BlueScreenView';   Folder = 'BlueScreenView';  Signer = 'Nir Sofer'
       Sources = @(
           @{ Kind = 'scoop'; Spec = 'Nirsoft/bluescreenview' }
           @{ Kind = 'url'; Url = 'https://www.nirsoft.net/utils/bluescreenview-x64.zip' }) }
    @{ Name = 'PawnIO setup';     Folder = 'PawnIO';          Signer = 'namazso.eu'
       Sources = @(
           @{ Kind = 'github'; Repo = 'namazso/PawnIO.Setup'; Asset = '^PawnIO_setup\.exe$' }
           # The API is rate-limited (60 requests/hour without a token); the plain "latest" link is not.
           @{ Kind = 'url'; Url = 'https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe' }) }
)

# ------------------------------------------------------------------ helpers

function Get-ScoopManifest([string]$Spec) {
    # raw.githubusercontent.com first; jsDelivr mirrors the same repositories if GitHub's raw host is down.
    $bucket, $name = $Spec -split '/', 2
    $urls = @(
        "https://raw.githubusercontent.com/ScoopInstaller/$bucket/master/bucket/$name.json"
        "https://cdn.jsdelivr.net/gh/ScoopInstaller/$bucket@master/bucket/$name.json"
    )
    $errors = @()
    foreach ($u in $urls) {
        try { return Invoke-RestMethod -Uri $u -UserAgent $UserAgent }
        catch { $errors += "$(([Uri]$u).Host): $($_.Exception.Message)" }
    }
    throw ("Could not read the Scoop manifest $Spec. " + ($errors -join '; '))
}

function Resolve-ScoopSource($Manifest) {
    $arch = $null
    if ($Manifest.architecture -and $Manifest.architecture.'64bit') { $arch = $Manifest.architecture.'64bit' }
    $url = if ($arch -and $arch.url) { $arch.url } else { $Manifest.url }
    $hash = if ($arch -and $arch.hash) { $arch.hash } else { $Manifest.hash }
    $extractDir = if ($arch -and $arch.extract_dir) { $arch.extract_dir } else { $Manifest.extract_dir }
    if ($url -is [array]) { $url = $url[0]; $hash = @($hash)[0] }
    if (-not $url) { throw 'The Scoop manifest has no download URL.' }
    [pscustomobject]@{ Version = "$($Manifest.version)"; Url = "$url"; Hash = "$hash"; ExtractDir = "$extractDir" }
}

function Split-DownloadUrl([string]$Url) {
    # Scoop's "url#/name" convention renames the downloaded file.
    $name = $null
    if ($Url -match '^(.*?)#/(.+)$') { $Url = $Matches[1]; $name = $Matches[2] }
    # sourceforge.net/projects/X/files/Y -> downloads.sourceforge.net/project/X/Y (direct mirror redirect).
    if ($Url -match '^https?://(?:www\.)?sourceforge\.net/projects?/([^/]+)/files/(.+?)(?:/download)?$') {
        $Url = "https://downloads.sourceforge.net/project/$($Matches[1])/$($Matches[2])"
    }
    if (-not $name) { $name = [IO.Path]::GetFileName(([Uri]$Url).AbsolutePath) }
    [pscustomobject]@{ Url = $Url; FileName = $name }
}

function Get-MirrorUrls([string]$Url, $Mirrors) {
    # The same path on each mirror host, without repeating a host already in the list (http and https count as one).
    $list = @($Url)
    foreach ($mirror in @($Mirrors)) {
        if ($mirror) { $list += $mirror.TrimEnd('/') + ([Uri]$Url).PathAndQuery }
    }
    $seen = @{}
    foreach ($u in $list) {
        $key = ([Uri]$u).Host.ToLowerInvariant() + ([Uri]$u).PathAndQuery
        if (-not $seen.ContainsKey($key)) { $seen[$key] = $true; $u }
    }
}

function Get-TechPowerUpLinks([string]$Page, [string]$Id, [int]$Max) {
    # Posting the file id returns the mirror list (closest first); posting id + server_id answers with a redirect
    # to a short-lived link on that mirror. Only the first few mirrors are asked, each with one request.
    $html = (Invoke-WebRequest -Uri $Page -Method Post -Body @{ id = $Id } -UseBasicParsing -UserAgent $UserAgent).Content
    $servers = @([regex]::Matches($html, 'name="server_id" value="(\d+)"') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    if ($servers.Count -eq 0) { throw 'The TechPowerUp download page listed no mirrors.' }

    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(30)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd($UserAgent)
    try {
        foreach ($server in $servers | Select-Object -First $Max) {
            try {
                $form = New-Object 'System.Collections.Generic.Dictionary[string,string]'
                $form['id'] = $Id
                $form['server_id'] = $server
                $response = $client.PostAsync($Page, (New-Object System.Net.Http.FormUrlEncodedContent($form))).GetAwaiter().GetResult()
                if ($response.Headers.Location) { $response.Headers.Location.AbsoluteUri }
            }
            catch {
                # This mirror did not answer; the next one might.
            }
        }
    }
    finally {
        $client.Dispose()
    }
}

function Resolve-Source($Source, $Tool) {
    # Turns a source entry into what to download: label, stamp, candidate URLs, hash, file name.
    # TechPowerUp links cost one request per mirror, so they are only fetched when a download is needed.
    switch ($Source.Kind) {
        'techpowerup' {
            $page = "https://www.techpowerup.com/download/$($Source.Page)/"
            $html = (Invoke-WebRequest -Uri $page -UseBasicParsing -UserAgent $UserAgent).Content
            # Versions are listed newest first; each file entry carries its name, hashes and the form's file id.
            foreach ($entry in [regex]::Matches($html, '(?s)<li class="file[^"]*">.*?</li>')) {
                $file = [regex]::Match($entry.Value, 'class="filename"[^>]*>\s*([^<]+?)\s*<').Groups[1].Value
                $m = [regex]::Match($file, $Source.File)
                if (-not $m.Success) { continue }
                $sha = [regex]::Match($entry.Value, '(?s)SHA256:</div>\s*<div class="hash-value">([0-9A-Fa-f]{64})<').Groups[1].Value
                $id = [regex]::Match($entry.Value, 'name="id" value="(\d+)"').Groups[1].Value
                if (-not $sha -or -not $id) { throw "The download page entry for $file has no SHA-256 or file id." }
                $version = $m.Groups['version'].Value
                return [pscustomobject]@{
                    Label = "version $version"; Stamp = "techpowerup:$version"; Hash = "sha256:$sha"; ExtractDir = ''
                    FileName = $(if ($Tool.SaveAs) { $Tool.SaveAs } else { $file })
                    Urls = @(); TechPowerUp = [pscustomobject]@{ Page = $page; Id = $id }
                }
            }
            throw "No file matching $($Source.File) on $page."
        }
        'scoop' {
            $s = Resolve-ScoopSource (Get-ScoopManifest $Source.Spec)
            $dl = Split-DownloadUrl $s.Url
            return [pscustomobject]@{
                Label = "version $($s.Version)"; Stamp = "scoop:$($s.Version)"; Hash = $s.Hash; ExtractDir = $s.ExtractDir
                FileName = $(if ($Tool.SaveAs) { $Tool.SaveAs } else { $dl.FileName }); Urls = @(Get-MirrorUrls $dl.Url $Source.Mirrors)
            }
        }
        'github' {
            $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$($Source.Repo)/releases/latest" -UserAgent $UserAgent
            $asset = @($rel.assets | Where-Object { $_.name -match $Source.Asset }) | Select-Object -First 1
            if (-not $asset) { throw "Release $($rel.tag_name) has no file matching $($Source.Asset)." }
            $hash = if ("$($asset.digest)" -match '^sha256:([0-9a-fA-F]{64})$') { "sha256:$($Matches[1])" } else { '' }
            return [pscustomobject]@{
                Label = "version $($rel.tag_name)"; Stamp = "github:$($rel.tag_name)"; Hash = $hash; ExtractDir = ''
                FileName = $asset.name; Urls = @($asset.browser_download_url)
            }
        }
        'url' {
            $dl = Split-DownloadUrl $Source.Url
            return [pscustomobject]@{
                Label = 'latest'; Stamp = (Get-RemoteStamp $Source.Url); Hash = ''; ExtractDir = ''
                FileName = $dl.FileName; Urls = @($dl.Url)
            }
        }
        default { throw "Unknown source kind '$($Source.Kind)'." }
    }
}

function Get-SourceName($Source) {
    switch ($Source.Kind) {
        'scoop' { "Scoop manifest $($Source.Spec)" }
        'techpowerup' { 'TechPowerUp download page' }
        'github' { "GitHub release $($Source.Repo)" }
        default { ([Uri]$Source.Url).Host }
    }
}

function Save-Download([string]$Url, [string]$Path) {
    Invoke-WebRequest -Uri $Url -OutFile $Path -UserAgent $UserAgent -UseBasicParsing -MaximumRedirection 10
    # A blocked or moved download often comes back as an HTML page with status 200.
    $buffer = New-Object byte[] 16
    $fs = [IO.File]::OpenRead($Path)
    try { $read = $fs.Read($buffer, 0, 16) } finally { $fs.Dispose() }
    $start = [Text.Encoding]::ASCII.GetString($buffer, 0, $read).TrimStart()
    if ($start.StartsWith('<')) { throw "The server returned a web page instead of the file ($Url)." }
}

function Save-VerifiedDownload([string[]]$Urls, [string]$Path, [string]$Hash) {
    # Tries each URL until one returns a real file that matches the hash. Returns the URL that worked.
    $errors = @()
    foreach ($url in $Urls) {
        try {
            Save-Download $url $Path
            if ($Hash -and -not (Test-FileHash $Path $Hash)) { throw 'hash mismatch' }
            return $url
        }
        catch {
            $errors += "$(([Uri]$url).Host): $($_.Exception.Message)"
            Remove-Item $Path -Force -ErrorAction SilentlyContinue
            if ($Urls.Count -gt 1) { Write-Host "    $(([Uri]$url).Host) failed ($($_.Exception.Message)), trying next mirror" -ForegroundColor DarkYellow }
        }
    }
    throw ('All download links failed. ' + ($errors -join '; '))
}

function Find-VerifiedCopy([string]$Folder, [string]$FileName, [string]$Hash) {
    # A single-file tool placed by hand counts as installed if it matches the manifest hash.
    if (-not $Hash -or -not (Test-Path $Folder)) { return $null }
    $ext = [IO.Path]::GetExtension($FileName)
    if ($ext -in '.zip', '.7z', '.rar') { return $null }
    foreach ($f in Get-ChildItem $Folder -File -Filter "*$ext") {
        if (Test-FileHash $f.FullName $Hash) { return $f.FullName }
    }
    return $null
}

function Test-FileHash([string]$Path, [string]$Expected) {
    if (-not $Expected) { return $true }
    $algorithm = 'SHA256'
    $value = $Expected
    if ($Expected -match '^(md5|sha1|sha256|sha512):(.+)$') { $algorithm = $Matches[1].ToUpperInvariant(); $value = $Matches[2] }
    (Get-FileHash -Path $Path -Algorithm $algorithm).Hash -eq $value.ToUpperInvariant()
}

function Get-RemoteStamp([string]$Url) {
    # For unversioned links: ETag/Last-Modified tell us whether the file changed since last time.
    try {
        $r = Invoke-WebRequest -Uri $Url -Method Head -UserAgent $UserAgent -UseBasicParsing -MaximumRedirection 10
        $etag = "$($r.Headers['ETag'])"
        $modified = "$($r.Headers['Last-Modified'])"
        if ($etag -or $modified) { return "url:$etag|$modified" }
    }
    catch { }
    return $null
}

function Get-SevenZip {
    if ($script:SevenZip) { return $script:SevenZip }
    $cmd = Get-Command 7z.exe -ErrorAction SilentlyContinue
    $candidates = @()
    if ($cmd) { $candidates += $cmd.Path }
    $candidates += "$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe", "$env:USERPROFILE\scoop\apps\7zip\current\7z.exe"
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { $script:SevenZip = $c; return $c }
    }

    # No 7-Zip on this PC: unpack the official MSI (administrative extract, nothing gets installed).
    Write-Host '    7-Zip not found; unpacking a temporary copy (nothing is installed)'
    $src = Resolve-ScoopSource (Get-ScoopManifest 'Main/7zip')
    $dl = Split-DownloadUrl $src.Url
    $msi = Join-Path $Work $dl.FileName
    Save-Download $dl.Url $msi
    if (-not (Test-FileHash $msi $src.Hash)) { throw '7-Zip download failed its hash check.' }
    $target = Join-Path $Work '7zip'
    $p = Start-Process msiexec.exe -ArgumentList @('/a', "`"$msi`"", '/qn', "TARGETDIR=`"$target`"") -Wait -PassThru
    $exe = Get-ChildItem $target -Recurse -Filter 7z.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p.ExitCode -ne 0 -or -not $exe) { throw "Could not unpack 7-Zip (msiexec exit code $($p.ExitCode))." }
    $script:SevenZip = $exe.FullName
    return $script:SevenZip
}

function Expand-Download([string]$File, [string]$Stage, [string]$ExtractDir, [string]$Pick) {
    $ext = [IO.Path]::GetExtension($File).ToLowerInvariant()
    $raw = Join-Path $Stage 'raw'
    New-Item -ItemType Directory -Force -Path $raw | Out-Null

    switch ($ext) {
        '.zip' {
            [IO.Compression.ZipFile]::ExtractToDirectory($File, $raw)
        }
        { $_ -in '.7z', '.rar' } {
            $7z = Get-SevenZip
            & $7z x $File "-o$raw" -y | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "7-Zip failed to extract $File (exit code $LASTEXITCODE)." }
        }
        default {
            Copy-Item $File (Join-Path $raw ([IO.Path]::GetFileName($File)))
        }
    }

    $source = if ($ExtractDir) { Join-Path $raw $ExtractDir } else { $raw }
    if (-not (Test-Path $source)) { throw "Expected folder '$ExtractDir' is missing from the download." }

    if ($Pick) {
        # Take one file out of a bigger package (smartctl.exe from the smartmontools installer), preferring 64-bit.
        $hit = Get-ChildItem $source -Recurse -Filter $Pick |
            Sort-Object @{ Expression = { $_.FullName -match '[\\/]bin32[\\/]' } }, @{ Expression = { $_.FullName.Length } } |
            Select-Object -First 1
        if (-not $hit) { throw "$Pick was not found in the download." }
        $picked = Join-Path $Stage 'picked'
        New-Item -ItemType Directory -Force -Path $picked | Out-Null
        Copy-Item $hit.FullName $picked
        return $picked
    }

    # Zips that wrap everything in a single top-level folder: use that folder's contents.
    $items = @(Get-ChildItem $source -Force)
    if ($items.Count -eq 1 -and $items[0].PSIsContainer) { return $items[0].FullName }
    return $source
}

function Get-SignerName([string]$Path) {
    $sig = Get-AuthenticodeSignature $Path
    if ($sig.Status -ne 'Valid' -or -not $sig.SignerCertificate) { return $null }
    $sig.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
}

function Assert-Signed([string]$Folder, [string]$Signer) {
    foreach ($exe in Get-ChildItem $Folder -Recurse -Include *.exe, *.dll) {
        $name = Get-SignerName $exe.FullName
        if ($name -ne $Signer) {
            $got = if ($name) { "signed by $name" } else { "status: $((Get-AuthenticodeSignature $exe.FullName).Status)" }
            throw "$($exe.Name) is not validly signed by $Signer ($got). Not installing it."
        }
    }
}

function Assert-FileHashes([string]$Folder, [hashtable]$Pinned) {
    # Every pinned file must be present and match, and no other executable code may come along with them.
    foreach ($rel in $Pinned.Keys) {
        $path = Join-Path $Folder $rel
        if (-not (Test-Path $path)) { throw "$rel is missing from the download." }
        $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash
        if (@($Pinned[$rel]) -notcontains $hash) {
            throw "$rel does not match a known build (SHA-256 $hash). If the author released a new version, add its hash to fetch-tools.ps1 after checking it."
        }
    }
    foreach ($f in Get-ChildItem $Folder -Recurse -File -Include *.exe, *.dll, *.sys, *.scr, *.com) {
        $rel = $f.FullName.Substring($Folder.TrimEnd('\', '/').Length).TrimStart('\', '/')
        if (-not ($Pinned.Keys | Where-Object { $_ -eq $rel })) { throw "$rel is not one of the pinned files. Not installing it." }
    }
}

function Add-Extras([string]$Folder, $Extras) {
    # Extras improve a tool but are not required, so a failure here is a warning, not a failed install.
    foreach ($x in @($Extras)) {
        if (-not $x) { continue }
        $target = Join-Path $Folder $x.Path
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        $file = ($x.File -split '/' | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
        $urls = @(
            "https://raw.githubusercontent.com/$($x.Repo)/$($x.Commit)/$file"
            "https://cdn.jsdelivr.net/gh/$($x.Repo)@$($x.Commit)/$file"
        )
        try {
            Save-VerifiedDownload $urls $target $x.Hash | Out-Null
            # Staged only: an existing .cfg in the kit is still kept by Merge-Folder.
            Write-Host "    fetched $($x.Path)"
        }
        catch {
            Write-Warning "    could not add $($x.Path): $($_.Exception.Message)"
        }
    }
}

function Merge-Folder([string]$From, [string]$To) {
    $count = 0
    foreach ($f in Get-ChildItem $From -Recurse -File) {
        $rel = $f.FullName.Substring($From.TrimEnd('\', '/').Length).TrimStart('\', '/')
        $target = Join-Path $To $rel
        if ((Test-Path $target) -and ($KeepExtensions -contains $f.Extension.ToLowerInvariant())) { continue }
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item $f.FullName $target -Force
        $count++
    }
    return $count
}

function Test-Present([string]$Folder) {
    if (-not (Test-Path $Folder)) { return $false }
    [bool](Get-ChildItem $Folder -Recurse -File | Where-Object { $_.Name -ne $StampName } | Select-Object -First 1)
}

function Update-Tool($t) {
    # Tries each source in turn; returns a result row, or throws when every source failed.
    $dest = Join-Path $ToolsRoot $t.Folder
    $stampFile = Join-Path $dest $StampName
    $present = Test-Present $dest
    $current = if (Test-Path $stampFile) { (Get-Content $stampFile -Raw).Trim() } else { '' }
    $sources = @($t.Sources)
    $errors = @()

    for ($i = 0; $i -lt $sources.Count; $i++) {
        $s = $sources[$i]
        $sourceName = Get-SourceName $s
        $more = $i -lt $sources.Count - 1
        try {
            $src = Resolve-Source $s $t
            if (-not $src.Hash -and -not $t.Signer -and -not $t.FileHashes) { throw 'This source has no hash and the tool has no signer or pinned hashes to check, so the download cannot be verified.' }
            $label = $src.Label
            # Name the fallback source in the summary; plain links already show their host while downloading.
            if ($i -gt 0) { $label += if ($s.Kind -eq 'url') { ' (fallback link)' } else { " via $sourceName" } }

            $upToDate = $present -and $src.Stamp -and ($current -eq $src.Stamp)
            if (-not $upToDate -and -not $Force -and $src.Hash) {
                $copy = Find-VerifiedCopy $dest $src.FileName $src.Hash
                if ($copy) {
                    if (-not $Check) { Set-Content -Path $stampFile -Value $src.Stamp -Encoding ASCII }
                    Write-Host "    up to date: $(Split-Path $copy -Leaf) matches the $($src.Label) hash"
                    return [pscustomobject]@{ Tool = $t.Name; Result = 'up to date'; Detail = "$label (verified local copy)" }
                }
            }

            if ($upToDate -and -not $Force) {
                Write-Host "    up to date ($label)"
                return [pscustomobject]@{ Tool = $t.Name; Result = 'up to date'; Detail = $label }
            }

            if ($Check) {
                $state = if (-not $present) { 'missing' } elseif (-not $src.Stamp) { 'unknown (no version info)' } else { 'update available' }
                Write-Host "    $state ($label)"
                return [pscustomobject]@{ Tool = $t.Name; Result = $state; Detail = $label }
            }

            $stage = Join-Path $Work ($t.Folder + "-$i")
            New-Item -ItemType Directory -Force -Path $stage | Out-Null
            $file = Join-Path $stage $src.FileName
            $urls = @(if ($src.TechPowerUp) { Get-TechPowerUpLinks $src.TechPowerUp.Page $src.TechPowerUp.Id 6 } else { $src.Urls })
            if ($urls.Count -eq 0) { throw 'No download link could be obtained.' }
            Write-Host "    downloading $label from $(([Uri]$urls[0]).Host)"
            $used = Save-VerifiedDownload $urls $file $src.Hash
            if ($used -ne $urls[0]) { Write-Host "    got it from $(([Uri]$used).Host)" }
            if ($src.Hash) { Write-Host '    hash verified' }

            $content = Expand-Download $file $stage $src.ExtractDir $t.Pick
            if ($t.Signer) {
                Assert-Signed $content $t.Signer
                Write-Host "    signature verified ($($t.Signer))"
            }
            if ($t.FileHashes) {
                Assert-FileHashes $content $t.FileHashes
                Write-Host '    files match the pinned hashes'
            }
            Add-Extras $content $t.Extras

            $copied = Merge-Folder $content $dest
            if ($src.Stamp) { Set-Content -Path $stampFile -Value $src.Stamp -Encoding ASCII }
            $verb = if ($present) { 'updated' } else { 'installed' }
            Write-Host "    $verb ($copied files)" -ForegroundColor Green
            return [pscustomobject]@{ Tool = $t.Name; Result = $verb; Detail = $label }
        }
        catch {
            $msg = $_.Exception.Message
            if ($msg -match 'being used by another process') { throw 'a file is in use. Close the tool and run this again.' }
            $errors += "$($sourceName): $msg"
            if ($more) { Write-Host "    $sourceName failed ($msg), trying the next source" -ForegroundColor DarkYellow }
        }
    }

    throw ($errors -join ' | ')
}

# ------------------------------------------------------------------ main

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$ToolsRoot = Join-Path $Kit 'tools'
New-Item -ItemType Directory -Force -Path $ToolsRoot | Out-Null
$Work = Join-Path ([IO.Path]::GetTempPath()) ('diagkit-fetch-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $Work | Out-Null
$results = New-Object System.Collections.Generic.List[object]

try {
    foreach ($t in $Tools) {
        if ($Only -and -not ($Only | Where-Object { $_ -eq $t.Name -or $_ -eq $t.Folder })) { continue }
        Write-Host $t.Name -ForegroundColor Cyan
        try {
            $results.Add((Update-Tool $t))
        }
        catch {
            $msg = $_.Exception.Message
            if ($t.Manual) { $msg += ' ' + $t.Manual }
            Write-Warning "    $($t.Name): $msg"
            $results.Add([pscustomobject]@{ Tool = $t.Name; Result = 'FAILED'; Detail = $msg })
        }
    }
}
finally {
    Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue
}

Write-Host ''
if ($results.Count -eq 0) {
    Write-Warning "No tool matched -Only $($Only -join ', '). Names: $(($Tools | ForEach-Object { $_.Name }) -join ', ')"
}
$results | Format-Table -AutoSize -Wrap | Out-String -Width 160 | Write-Host
if ($results | Where-Object Result -eq 'FAILED') { exit 1 }
