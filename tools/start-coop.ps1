# Launch two local windows over the PwA broker transport (not the official -fastmp loopback).
# host window runs the in-process broker relay on 127.0.0.1:33772; client window connects to it.
param(
    [string]$GameRoot = 'D:\Programs\Steam\steamapps\common\Slay the Spire 2',
    [int]$BrokerPort = 33772,
    [string]$Session = 'pwa',
    [int]$HostClientId = 1,
    [int]$JoinClientId = 1001,
    [int]$ClientIndex = 1,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $GameRoot 'SlayTheSpire2.exe'
if (-not (Test-Path $exe)) { throw "game exe not found: $exe" }

if (-not $KeepRunning) {
    Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 4
}

$env:PWA_COOP = 'host'
$env:PWA_COOP_INDEX = '0'
$env:PWA_COOP_ENDPOINT = "127.0.0.1:$BrokerPort"
$env:PWA_COOP_SESSION = $Session
Start-Process -FilePath $exe -WorkingDirectory $GameRoot -ArgumentList @(
    '--force-steam','off','--clientId',"$HostClientId",'-fastmp','host_standard'
) | Out-Null
Write-Output "[coop] host started (clientId=$HostClientId, broker=$BrokerPort, session=$Session)"
Start-Sleep -Seconds 20

$env:PWA_COOP = 'client'
$env:PWA_COOP_INDEX = "$ClientIndex"
Start-Process -FilePath $exe -WorkingDirectory $GameRoot -ArgumentList @(
    '--windowed','--force-steam','off','--clientId',"$JoinClientId",'-fastmp','join'
) | Out-Null
Write-Output "[coop] client started (clientId=$JoinClientId, index=$ClientIndex)"

Remove-Item Env:\PWA_COOP -ErrorAction SilentlyContinue
Remove-Item Env:\PWA_COOP_INDEX -ErrorAction SilentlyContinue
Remove-Item Env:\PWA_COOP_ENDPOINT -ErrorAction SilentlyContinue
Remove-Item Env:\PWA_COOP_SESSION -ErrorAction SilentlyContinue
Start-Sleep -Seconds 15
Write-Output '[coop] ports: host=8091 / client=8092 (World prefers the client)'
