param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Message = "",
    [switch]$NoCommit
)

# One snapshot per release: git commit + tag, plus a copy of what is installed in the game.
# ASCII only: Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI.
$ErrorActionPreference = "Stop"
$git = "D:\Program Files\coopanion\resources\app\mingit\cmd\git.exe"
if (-not (Test-Path $git)) { $git = "git" }

$root = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = Join-Path $root "backups\$Tag-$stamp"
$gameMods = "D:\Programs\Steam\steamapps\common\Slay the Spire 2\mods\PwABridge"

New-Item -ItemType Directory -Force -Path $backup | Out-Null
if (Test-Path $gameMods) {
    Copy-Item -Recurse -Force $gameMods (Join-Path $backup "installed-PwABridge")
}

Set-Location $root
if (-not $NoCommit) {
    & $git add -A
    $msg = if ($Message) { $Message } else { "release $Tag" }
    & $git commit -m $msg
    if ($LASTEXITCODE -ne 0) { Write-Host "[backup] nothing to commit" }
    & $git tag -f $Tag
}

& $git log --oneline -n 5
Write-Host "[backup] archived to $backup"
