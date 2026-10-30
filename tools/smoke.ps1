param(
    [string]$Base = "http://127.0.0.1:8090",
    [switch]$SkipNavigate,
    [switch]$Restart,
    [string]$GameRoot = "D:\Programs\Steam\steamapps\common\Slay the Spire 2"
)

# End-to-end smoke test against a running game window.
# ASCII only: Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI.
$ErrorActionPreference = "Stop"

function Get-State { Invoke-RestMethod -Uri "$Base/state" -TimeoutSec 15 }
function Invoke-Act([hashtable]$Payload) {
    $json = $Payload | ConvertTo-Json -Compress -Depth 8
    Invoke-RestMethod -Uri "$Base/act" -Method Post -ContentType "application/json" -Body $json -TimeoutSec 40
}
function Show([string]$Label) {
    $s = Get-State
    Write-Host ("{0,-22} screen={1,-18} ac={2,-12} floor={3}" -f $Label, $s.screen, (($s.actions) -join ","), $s.run.floor)
    return $s
}

if ($Restart) {
    Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "install-mod.ps1") -NoBuild
    Start-Process -FilePath (Join-Path $GameRoot "SlayTheSpire2.exe") `
        -ArgumentList "--force-steam", "off", "--clientId", "1001" -WorkingDirectory $GameRoot
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        try {
            $h = Invoke-RestMethod -Uri "$Base/health" -TimeoutSec 2
            if ($h.screen -ne "UNKNOWN" -and $h.screen -ne $null -and $h.screen -ne "") { break }
        } catch { }
    }
    Start-Sleep -Seconds 1
}

$state = Show "start"

if (-not $SkipNavigate) {
    if ($state.screen -eq "MAIN_MENU") {
        $null = Invoke-Act @{ action = "open_singleplayer" }
        Start-Sleep -Milliseconds 500
        $state = Show "singleplayer menu"
    }

    if ($state.screen -eq "SINGLEPLAYER_MENU") {
        $null = Invoke-Act @{ action = "start_standard_run" }
        Start-Sleep -Milliseconds 700
        $state = Show "character select"
    }

    if ($state.screen -eq "CHARACTER_SELECT") {
        $null = Invoke-Act @{ action = "embark" }
        Start-Sleep -Seconds 3
        $state = Show "run start"
    }
}

# Neow: take the first unlocked option, then drive any card selection it opens.
if ($state.screen -eq "EVENT") {
    $option = 0
    $null = Invoke-Act @{ action = "choose_event_option"; index = $option }
    Start-Sleep -Milliseconds 800
    $state = Show "after event option"

    while ($state.screen -eq "CARD_SELECTION") {
        $null = Invoke-Act @{ action = "select_card"; index = 0 }
        Start-Sleep -Milliseconds 400
        $sel = (Get-State).selection
        if ($sel -and $sel.confirm_available) {
            $null = Invoke-Act @{ action = "confirm_selection" }
        }
        Start-Sleep -Milliseconds 600
        $state = Show "selection handled"
    }

    if ($state.screen -eq "EVENT") {
        $null = Invoke-Act @{ action = "choose_event_option" }
        Start-Sleep -Milliseconds 800
        $state = Show "event finished"
    }
}

Write-Host "smoke done."
