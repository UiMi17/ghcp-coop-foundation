#Requires -Version 5.1
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

$gameDir = $env:GHPC_GAME_DIR
if (-not $gameDir) {
    $propsPath = Join-Path $repoRoot "Directory.Build.props"
    if (Test-Path -LiteralPath $propsPath) {
        [xml]$x = Get-Content -LiteralPath $propsPath
        $gameDir = $x.Project.PropertyGroup.GHPCGameDir.Trim()
    }
}
if (-not $gameDir) { throw "Set GHPC_GAME_DIR or define GHPCGameDir in Directory.Build.props" }

$assembly = Join-Path $gameDir "GHPC_Data\Managed\Assembly-CSharp.dll"
if (-not (Test-Path -LiteralPath $assembly)) { throw "Not found: $assembly" }

$ilspy = Get-Command ilspycmd -ErrorAction SilentlyContinue
if (-not $ilspy) {
    Write-Host "ilspycmd not on PATH. Install: dotnet tool install -g ilspycmd"
    exit 1
}

$outDir = Join-Path $repoRoot "artifacts\decompiled"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Write-Host "Decompiling to $outDir ..."
& ilspycmd $assembly -o $outDir -p
Write-Host "Done."
