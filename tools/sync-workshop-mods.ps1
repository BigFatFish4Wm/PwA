# Link Steam Workshop mods (appid 2868840) into the game's mods\ folder as junctions,
# so they load even when the game runs with --force-steam off (PwA co-op / offline).
#
#   powershell -ExecutionPolicy Bypass -File tools\sync-workshop-mods.ps1            # link all
#   powershell -ExecutionPolicy Bypass -File tools\sync-workshop-mods.ps1 -Only silentSkin,necrobinderSkin
#   powershell -ExecutionPolicy Bypass -File tools\sync-workshop-mods.ps1 -WhatIf
#   powershell -ExecutionPolicy Bypass -File tools\sync-workshop-mods.ps1 -Remove    # remove our junctions
param(
    [string]$GameRoot = 'D:\Programs\Steam\steamapps\common\Slay the Spire 2',
    [string]$WorkshopAppId = '2868840',
    [string]$Only = '',
    [switch]$Remove,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$modsDir = Join-Path $GameRoot 'mods'
if (-not (Test-Path $modsDir)) { throw "mods dir not found: $modsDir" }

$steamApps = Split-Path (Split-Path $GameRoot -Parent) -Parent
$workshopRoot = Join-Path $steamApps "workshop\content\$WorkshopAppId"
if (-not (Test-Path $workshopRoot)) { throw "workshop dir not found: $workshopRoot" }

$onlyIds = @()
if ($Only) { $onlyIds = $Only.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ } }

function Find-Manifest([string]$itemDir) {
    $candidates = @()
    $candidates += Get-ChildItem $itemDir -Filter '*.json' -File -ErrorAction SilentlyContinue
    $candidates += Get-ChildItem $itemDir -Filter '*.json' -File -Recurse -Depth 1 -ErrorAction SilentlyContinue
    foreach ($file in $candidates) {
        try {
            $json = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
            if ($json.id) { return @{ Id = [string]$json.id; Dir = $file.Directory.FullName } }
        } catch {
            # not a manifest
        }
    }
    return $null
}

if ($Remove) {
    $removed = 0
    Get-ChildItem $modsDir -ErrorAction SilentlyContinue | Where-Object { $_.LinkType -eq 'Junction' } | ForEach-Object {
        $target = @($_.Target)[0]
        if ($target -and $target.StartsWith($workshopRoot, [StringComparison]::OrdinalIgnoreCase)) {
            if ($WhatIf) { Write-Output "[whatif] remove $($_.Name) -> $target" }
            else { cmd /c rmdir "`"$($_.FullName)`"" | Out-Null; Write-Output "removed $($_.Name)" }
            $removed++
        }
    }
    Write-Output "[sync] removed $removed junction(s)"
    return
}

$added = 0; $skipped = 0; $failed = 0
foreach ($item in Get-ChildItem $workshopRoot -Directory -ErrorAction SilentlyContinue) {
    $manifest = Find-Manifest $item.FullName
    if (-not $manifest) { continue }
    $id = $manifest.Id
    if ($onlyIds.Count -gt 0 -and $onlyIds -notcontains $id) { continue }
    if ($id -notmatch '^[A-Za-z0-9_.-]+$') { Write-Output "[skip] bad id '$id' in $($item.Name)"; $skipped++; continue }
    if ($id -eq 'PwABridge') { continue }

    $link = Join-Path $modsDir $id
    $target = $manifest.Dir
    if (Test-Path $link) {
        $existing = Get-Item $link -Force
        if ($existing.LinkType -eq 'Junction' -and @($existing.Target)[0] -eq $target) { $skipped++; continue }
        if ($existing.LinkType -eq 'Junction') {
            # 指向旧版本（工坊更新后目录可能变）：重指
            if ($WhatIf) { Write-Output "[whatif] relink $id -> $target" }
            else { cmd /c rmdir "`"$link`"" | Out-Null; New-Item -ItemType Junction -Path $link -Target $target | Out-Null; Write-Output "relinked $id" }
            $added++
            continue
        }
        Write-Output "[skip] $id already exists (not our junction)"
        $skipped++
        continue
    }

    if ($WhatIf) { Write-Output "[whatif] link $id -> $target" }
    else {
        try {
            New-Item -ItemType Junction -Path $link -Target $target | Out-Null
            Write-Output "linked $id"
        } catch {
            Write-Output "[fail] $id : $($_.Exception.Message)"
            $failed++
            continue
        }
    }
    $added++
}
Write-Output "[sync] added=$added skipped=$skipped failed=$failed workshop=$workshopRoot"
