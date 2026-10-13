<#
.SYNOPSIS
    Builds DiagKit and downloads its tools into a portable folder you can copy to a USB stick.

.EXAMPLE
    .\build.ps1                      # complete kit in .\dist\DiagKit: DiagKit.exe plus every tool
    .\build.ps1 -Output E:\DiagKit   # build straight onto the stick
    .\build.ps1 -NoTools             # only the app; run .\fetch-tools.ps1 later
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot 'dist\DiagKit'),
    [switch]$NoTools
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'App\DiagKit.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is not installed. Get it from https://dotnet.microsoft.com/download'
}

# DiagKit targets .NET Framework 4.8, which ships with Windows, so the build is one small exe with its libraries embedded.
$build = Join-Path ([IO.Path]::GetTempPath()) ('diagkit-build-' + [guid]::NewGuid().ToString('N'))
Write-Host "Building DiagKit ..." -ForegroundColor Cyan
& dotnet build $project -c Release -o $build
if ($LASTEXITCODE -ne 0) { throw 'Build failed; see the output above.' }
New-Item -ItemType Directory -Force -Path $Output | Out-Null
Copy-Item (Join-Path $build 'DiagKit.exe') $Output -Force
Remove-Item -Recurse -Force $build

# Create the tools\ folders now so they can be filled before the first launch (DiagKit also does this itself).
$manifest = Get-Content (Join-Path $PSScriptRoot 'App\tools.default.json') -Raw
$folders = [regex]::Matches($manifest, '"folder"\s*:\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
foreach ($f in $folders) { New-Item -ItemType Directory -Force -Path (Join-Path $Output "tools\$f") | Out-Null }
Copy-Item (Join-Path $PSScriptRoot 'TOOLS.md') (Join-Path $Output 'tools\README.md') -Force

$exe = Join-Path $Output 'DiagKit.exe'
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Built: $exe ($size MB)" -ForegroundColor Green
Write-Host "Rebuilding later never touches tools\ or reports\."

if ($NoTools) {
    Write-Host "Skipped the tools. .\fetch-tools.ps1 -Kit '$Output' downloads or updates them (see tools\README.md)."
    return
}

# One command gives a complete kit. fetch-tools only downloads what is missing or has a new version,
# so running the build again is cheap.
Write-Host ""
Write-Host "Fetching the tools ..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'fetch-tools.ps1') -Kit $Output
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Some tools could not be downloaded (see the table above). DiagKit works without them; run .\fetch-tools.ps1 -Kit '$Output' again later."
    exit 1
}
Write-Host "Kit ready: $Output" -ForegroundColor Green
