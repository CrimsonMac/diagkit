<#
.SYNOPSIS
    Downloads or updates the portable tools in a DiagKit folder.

.DESCRIPTION
    Most tools have versioned download URLs, so instead of hard-coding them this script reads the
    community-maintained Scoop manifests (github.com/ScoopInstaller), which carry the current URL and a
    hash for each release. Scoop itself is NOT needed or installed. Every download is verified:
      - Scoop sources: against the manifest's hash
      - Sysinternals: against Microsoft's Authenticode signature
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
$UserAgent = 'DiagKit-fetch/1.0 (PowerShell)'
$KeepExtensions = @('.ini', '.cfg')
$StampName = '.diagkit-source'

# Folder names match tools.json, so DiagKit finds everything on the next Rescan.
$Tools = @(
    @{ Name = 'HWiNFO64';         Folder = 'HWiNFO64';        Scoop = 'Extras/hwinfo' }
    @{ Name = 'CPU-Z';            Folder = 'CPU-Z';           Scoop = 'Extras/cpu-z' }
    # TechPowerUp's mirrors refuse some regions/networks (404 or an HTML page); the hash makes trying others safe.
    @{ Name = 'GPU-Z';            Folder = 'GPU-Z';           Scoop = 'Extras/gpu-z'
       Mirrors = @('https://us1-dl.techpowerup.com', 'https://us2-dl.techpowerup.com')
       Manual = 'Download it once from https://www.techpowerup.com/gpuz/ into tools\GPU-Z; the next run checks it against the same hash and marks it up to date.' }
    @{ Name = 'CrystalDiskInfo';  Folder = 'CrystalDiskInfo'; Scoop = 'Extras/crystaldiskinfo' }
    @{ Name = 'CrystalDiskMark';  Folder = 'CrystalDiskMark'; Scoop = 'Extras/crystaldiskmark' }
    @{ Name = 'WizTree';          Folder = 'WizTree';         Scoop = 'Extras/wiztree' }
    @{ Name = 'Everything';       Folder = 'Everything';      Scoop = 'Extras/everything' }
    @{ Name = 'smartctl';         Folder = 'smartmontools';   Scoop = 'Main/smartmontools'; Pick = 'smartctl.exe' }
    @{ Name = 'Autoruns';         Folder = 'Autoruns';        Url = 'https://download.sysinternals.com/files/Autoruns.zip';        Signer = 'Microsoft Corporation' }
    @{ Name = 'Process Explorer'; Folder = 'ProcessExplorer'; Url = 'https://download.sysinternals.com/files/ProcessExplorer.zip'; Signer = 'Microsoft Corporation' }
    @{ Name = 'RAMMap';           Folder = 'RAMMap';          Url = 'https://download.sysinternals.com/files/RAMMap.zip';          Signer = 'Microsoft Corporation' }
    @{ Name = 'BlueScreenView';   Folder = 'BlueScreenView';  Url = 'https://www.nirsoft.net/utils/bluescreenview-x64.zip' }
    @{ Name = 'PawnIO setup';     Folder = 'PawnIO';          Url = 'https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe' }
)

# ------------------------------------------------------------------ helpers

function Get-ScoopManifest([string]$Spec) {
    $bucket, $name = $Spec -split '/', 2
    Invoke-RestMethod -Uri "https://raw.githubusercontent.com/ScoopInstaller/$bucket/master/bucket/$name.json" -UserAgent $UserAgent
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
    throw ('All download sources failed. ' + ($errors -join '; '))
}

function Find-VerifiedCopy([string]$Folder, [string]$FileName, [string]$Hash) {
    # A single-file tool placed by hand counts as installed if it matches the manifest hash.
    if (-not $Hash -or -not (Test-Path $Folder)) { return $null }
    $ext = [IO.Path]::GetExtension($FileName)
    if ($ext -in '.zip', '.7z') { return $null }
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
        '.7z' {
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

function Assert-Signed([string]$Folder, [string]$Signer) {
    foreach ($exe in Get-ChildItem $Folder -Recurse -Include *.exe, *.dll) {
        $sig = Get-AuthenticodeSignature $exe.FullName
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch [regex]::Escape("CN=$Signer")) {
            throw "$($exe.Name) is not validly signed by $Signer (status: $($sig.Status)). Not installing it."
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
        $dest = Join-Path $ToolsRoot $t.Folder
        $stampFile = Join-Path $dest $StampName
        $present = Test-Present $dest
        $current = if (Test-Path $stampFile) { (Get-Content $stampFile -Raw).Trim() } else { '' }

        try {
            if ($t.Scoop) {
                $src = Resolve-ScoopSource (Get-ScoopManifest $t.Scoop)
                $stamp = "scoop:$($src.Version)"
                $label = "version $($src.Version)"
            }
            else {
                $src = [pscustomobject]@{ Version = ''; Url = $t.Url; Hash = ''; ExtractDir = '' }
                $stamp = Get-RemoteStamp $t.Url
                $label = 'latest'
            }

            $upToDate = $present -and $stamp -and ($current -eq $stamp)
            if (-not $upToDate -and -not $Force -and $src.Hash) {
                $dlName = (Split-DownloadUrl $src.Url).FileName
                $copy = Find-VerifiedCopy $dest $dlName $src.Hash
                if ($copy) {
                    if (-not $Check) { Set-Content -Path $stampFile -Value $stamp -Encoding ASCII }
                    Write-Host "    up to date: $(Split-Path $copy -Leaf) matches the $label hash"
                    $results.Add([pscustomobject]@{ Tool = $t.Name; Result = 'up to date'; Detail = "$label (verified local copy)" })
                    continue
                }
            }

            if ($upToDate -and -not $Force) {
                Write-Host "    up to date ($label)"
                $results.Add([pscustomobject]@{ Tool = $t.Name; Result = 'up to date'; Detail = $label })
                continue
            }

            if ($Check) {
                $state = if (-not $present) { 'missing' } elseif (-not $stamp) { 'unknown (no version info)' } else { 'update available' }
                Write-Host "    $state ($label)"
                $results.Add([pscustomobject]@{ Tool = $t.Name; Result = $state; Detail = $label })
                continue
            }

            $dl = Split-DownloadUrl $src.Url
            $stage = Join-Path $Work $t.Folder
            New-Item -ItemType Directory -Force -Path $stage | Out-Null
            $file = Join-Path $stage $dl.FileName
            $urls = @($dl.Url)
            foreach ($mirror in @($t.Mirrors)) {
                if ($mirror) { $urls += $mirror.TrimEnd('/') + ([Uri]$dl.Url).PathAndQuery }
            }
            $urls = @($urls | Select-Object -Unique)
            Write-Host "    downloading $label from $(([Uri]$dl.Url).Host)"
            $used = Save-VerifiedDownload $urls $file $src.Hash
            if ($used -ne $dl.Url) { Write-Host "    got it from $(([Uri]$used).Host)" }
            if ($src.Hash) { Write-Host '    hash verified' }

            $content = Expand-Download $file $stage $src.ExtractDir $t.Pick
            if ($t.Signer) {
                Assert-Signed $content $t.Signer
                Write-Host "    signature verified ($($t.Signer))"
            }

            $copied = Merge-Folder $content $dest
            if ($stamp) { Set-Content -Path $stampFile -Value $stamp -Encoding ASCII }
            $verb = if ($present) { 'updated' } else { 'installed' }
            Write-Host "    $verb ($copied files)" -ForegroundColor Green
            $results.Add([pscustomobject]@{ Tool = $t.Name; Result = $verb; Detail = $label })
        }
        catch {
            $msg = $_.Exception.Message
            if ($msg -match 'being used by another process') { $msg = 'a file is in use. Close the tool and run this again.' }
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
if (-not $Only) {
    Write-Host 'TestMem5 is not available from any download source this script can verify; copy TM5.exe and its bin folder into tools\TestMem5 once (see TOOLS.md).'
}
if ($results | Where-Object Result -eq 'FAILED') { exit 1 }
