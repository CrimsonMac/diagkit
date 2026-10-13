<#
.SYNOPSIS
    Builds DiagKit and downloads its tools into a portable folder you can copy to a USB stick.

.EXAMPLE
    .\build.ps1                      # complete kit in .\dist\DiagKit: DiagKit.exe plus every tool
    .\build.ps1 -Output E:\DiagKit   # build straight onto the stick
    .\build.ps1 -Folder              # loose framework files: fastest startup, ~400 files
    .\build.ps1 -NoTools             # only the app; run .\fetch-tools.ps1 later
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot 'dist\DiagKit'),
    [switch]$Folder,
    [switch]$NoTools
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'App\DiagKit.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is not installed. Get the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0'
}
$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
    throw "No .NET 10 SDK found (installed: $($sdks -join ', ')). Install it from https://dotnet.microsoft.com/download/dotnet/10.0"
}

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', $Output,
    # Precompiled code: the main reason the app opens quickly instead of JIT-compiling WPF on every launch.
    '-p:PublishReadyToRun=true',
    '-p:DebugType=none'
)
if (-not $Folder) {
    $publishArgs += @(
        '-p:PublishSingleFile=true',
        # WPF's native DLLs (wpfgfx_cor3, PresentationNative_cor3, ...) cannot load from inside the bundle.
        # Bundling them makes the host extract them once per PC to %TEMP%\.net and reuse them after that.
        # Leaving them as loose files proved fragile: a publish without them crashes at window creation.
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        # Compression would shrink the exe but costs decompression time at every start.
        '-p:EnableCompressionInSingleFile=false'
    )
}

Write-Host "Publishing DiagKit to $Output ..." -ForegroundColor Cyan
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed; see the output above.' }

# Create the tools\ folders now so they can be filled before the first launch (DiagKit also does this itself).
$manifest = Get-Content (Join-Path $PSScriptRoot 'App\tools.default.json') -Raw
$folders = [regex]::Matches($manifest, '"folder"\s*:\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
foreach ($f in $folders) { New-Item -ItemType Directory -Force -Path (Join-Path $Output "tools\$f") | Out-Null }
Copy-Item (Join-Path $PSScriptRoot 'TOOLS.md') (Join-Path $Output 'tools\README.md') -Force

$exe = Join-Path $Output 'DiagKit.exe'
$size = [math]::Round((Get-Item $exe).Length / 1MB)
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
