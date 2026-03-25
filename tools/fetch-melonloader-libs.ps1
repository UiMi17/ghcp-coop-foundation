#Requires -Version 5.1
$ErrorActionPreference = "Stop"
$version = if ($env:MELONLOADER_VERSION) { $env:MELONLOADER_VERSION } else { "v0.7.2" }
$zipUrl = "https://github.com/LavaGang/MelonLoader/releases/download/$version/MelonLoader.x64.zip"
$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repoRoot "lib\MelonLoader"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("ml-" + [Guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$zipPath = Join-Path $tmp "MelonLoader.x64.zip"

Write-Host "Downloading $zipUrl"
Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath
Expand-Archive -LiteralPath $zipPath -DestinationPath $tmp -Force

$net35 = Join-Path $tmp "MelonLoader\net35"
$files = @(
    "MelonLoader.dll",
    "0Harmony.dll",
    "MonoMod.RuntimeDetour.dll",
    "MonoMod.Utils.dll",
    "MonoMod.Backports.dll",
    "MonoMod.ILHelpers.dll"
)
foreach ($f in $files) {
    $src = Join-Path $net35 $f
    if (-not (Test-Path -LiteralPath $src)) { throw "Missing in zip: $f" }
    Copy-Item -LiteralPath $src -Destination (Join-Path $outDir $f) -Force
    Write-Host "Installed $f"
}

Remove-Item -LiteralPath $tmp -Recurse -Force
Write-Host "MelonLoader compile references ready in: $outDir"
