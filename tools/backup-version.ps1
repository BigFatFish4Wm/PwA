param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Message = "",
    [switch]$NoCommit,
    [string]$Coopanion = "D:\Program Files\coopanion"
)

# One snapshot per release: git commit + tag, plus a copy of what is installed.
#   installed-PwABridge\   the mod DLL/config currently in the game
#   installed-worlds\      the World packages Coopanion actually loads (junctions resolved)
#   config.json            the deployment config (worlds.*.enabled lives here)
#   SNAPSHOT.md            where the snapshot came from and how to restore it
# ASCII only: Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI.
$ErrorActionPreference = "Stop"
$git = "D:\Program Files\coopanion\resources\app\mingit\cmd\git.exe"
if (-not (Test-Path $git)) { $git = "git" }

$root = Split-Path -Parent $PSScriptRoot
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = Join-Path $root "backups\$Tag-$stamp"
$gameMods = "D:\Programs\Steam\steamapps\common\Slay the Spire 2\mods\PwABridge"
$extensions = Join-Path $Coopanion "data\extensions\node_modules"
$deploymentConfig = Join-Path $Coopanion "data\home\companion\config.json"

New-Item -ItemType Directory -Force -Path $backup | Out-Null

# ---- what is installed in the game
if (Test-Path $gameMods) {
    Copy-Item -Recurse -Force $gameMods (Join-Path $backup "installed-PwABridge")
}

# ---- what Coopanion loads as Worlds (the extension dir is a set of junctions;
#      resolve them so the snapshot is real files, not links back to the repo)
$worlds = @()
foreach ($name in @("cortico-world-pwa", "cortico-world-memory")) {
    $link = Join-Path $extensions $name
    if (-not (Test-Path $link)) { continue }
    $item = Get-Item $link -Force
    $source = if ($item.Target) { @($item.Target)[0] } else { $link }
    $destination = Join-Path $backup "installed-worlds\$name"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -Recurse -Force $source $destination
    $worlds += [pscustomobject]@{ Name = $name; Link = $link; Source = $source }
}

# ---- deployment config (worlds.*.enabled, ports, secrets stay in .env)
if (Test-Path $deploymentConfig) {
    Copy-Item -Force $deploymentConfig (Join-Path $backup "config.json")
}

# ---- provenance
$commit = ""
$previousTags = ""
Set-Location $root
try { $commit = (& $git rev-parse --short HEAD 2>$null) } catch { }
try { $previousTags = (& $git tag --points-at HEAD 2>$null) -join ", " } catch { }

$lines = @(
    "# Snapshot $Tag ($stamp)",
    "",
    "Repo: $root",
    "Tag: $Tag (moved onto the commit that adds this snapshot)",
    "HEAD before this snapshot: $commit",
    "Tags on that commit: $previousTags",
    "",
    "## Mod",
    ("- " + $(if (Test-Path $gameMods) { "copied from $gameMods" } else { "not installed at $gameMods" })),
    "",
    "## Worlds"
)
foreach ($world in $worlds) {
    $lines += "- $($world.Name): $($world.Link) -> $($world.Source)"
}
if (-not $worlds.Count) { $lines += "- none linked" }
$lines += @(
    "",
    "## Restore",
    "",
    "1. Check out the tag above (git checkout $Tag).",
    "2. Mod: copy installed-PwABridge\* back to $gameMods, then restart the game.",
    "3. World: copy installed-worlds\<name> over the extension directory, or re-link",
    "   with: powershell -ExecutionPolicy Bypass -File tools\link-world.ps1 -Name <name>",
    "4. Config: copy config.json back to $deploymentConfig (it holds worlds.*.enabled).",
    "5. Restart Coopanion so the Worlds are loaded again.",
    "",
    "Note: the memory World's index lives in the workspace (<workspace>\.index\memory.db)",
    "and is not part of this snapshot; it rebuilds from the markdown files on the next scan."
)
Set-Content -Path (Join-Path $backup "SNAPSHOT.md") -Value $lines -Encoding utf8

if (-not $NoCommit) {
    & $git add -A
    $msg = if ($Message) { $Message } else { "release $Tag" }
    & $git commit -m $msg
    if ($LASTEXITCODE -ne 0) { Write-Host "[backup] nothing to commit" }
    & $git tag -f $Tag
}

& $git log --oneline -n 5
Write-Host "[backup] archived to $backup"
