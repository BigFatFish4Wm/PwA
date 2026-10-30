param(
    [string]$Configuration = "Debug",
    [string]$GameRoot = "D:\Programs\Steam\steamapps\common\Slay the Spire 2",
    [switch]$NoBuild
)

# ASCII only on purpose: Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "PwA\PwA.csproj"
$modName = "PwABridge"
$installDir = Join-Path $GameRoot "mods\$modName"

if (-not $NoBuild) {
    Write-Host "[install] building $project"
    dotnet build $project -c $Configuration -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
}

$dll = Join-Path $root "PwA\bin\$Configuration\net9.0\$modName.dll"
if (-not (Test-Path $dll)) { throw "built dll not found: $dll" }

New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item -Force $dll (Join-Path $installDir "$modName.dll")
Copy-Item -Force (Join-Path $root "PwA\mod_manifest.json") (Join-Path $installDir "$modName.json")

# Exactly one manifest per mod folder. mod_id.json is for the flat layout only; do not install it.
$stale = Join-Path $installDir "mod_id.json"
if (Test-Path $stale) { Remove-Item -Force $stale }

Write-Host "[install] installed to $installDir"
Get-ChildItem $installDir | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
Write-Host "[install] restart the game: DLLs are not hot-reloaded."
