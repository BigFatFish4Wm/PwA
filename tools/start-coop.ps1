# Launch local windows over the PwA broker transport (not the official -fastmp loopback).
# host runs the in-process broker relay; clients connect to it.
#
# 2 players (you + agent):
#   powershell -ExecutionPolicy Bypass -File tools\start-coop.ps1
# 3 players (you + agent + a remote player who installs the same mod):
#   powershell -ExecutionPolicy Bypass -File tools\start-coop.ps1 -Bind 0.0.0.0 -Token 'choose-a-secret' -Clients 2
#   remote player (on their own machine):
#     set PWA_COOP=client
#     set PWA_COOP_INDEX=2
#     set PWA_COOP_ENDPOINT=<your-ip>:33772
#     set PWA_COOP_TOKEN=choose-a-secret
#     SlayTheSpire2.exe --force-steam off --clientId 1002 -fastmp join
param(
    [string]$GameRoot = 'D:\Programs\Steam\steamapps\common\Slay the Spire 2',
    [int]$BrokerPort = 33772,
    [string]$Session = 'pwa',
    [string]$Bind = '127.0.0.1',
    [string]$Token = '',
    [int]$Clients = 1,
    [int]$HostClientId = 1,
    [int]$FirstJoinClientId = 1001,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $GameRoot 'SlayTheSpire2.exe'
if (-not (Test-Path $exe)) { throw "game exe not found: $exe" }
if ($Clients -lt 1 -or $Clients -gt 3) { throw 'Clients must be 1..3' }

if ($WithWorkshopMods) {
    & (Join-Path $PSScriptRoot 'sync-workshop-mods.ps1') -GameRoot $GameRoot | Select-Object -Last 3
}

if (-not $KeepRunning) {
    Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 4
}

$env:PWA_COOP = 'host'
$env:PWA_COOP_INDEX = '0'
$env:PWA_COOP_ENDPOINT = "127.0.0.1:$BrokerPort"
$env:PWA_COOP_SESSION = $Session
$env:PWA_COOP_BIND = $Bind
if ($Token) { $env:PWA_COOP_TOKEN = $Token } else { Remove-Item Env:\PWA_COOP_TOKEN -ErrorAction SilentlyContinue }
Start-Process -FilePath $exe -WorkingDirectory $GameRoot -ArgumentList @(
    '--force-steam','off','--clientId',"$HostClientId",'-fastmp','host_standard'
) | Out-Null
Write-Output "[coop] host started (clientId=$HostClientId, bind=$Bind, broker=$BrokerPort, session=$Session, token=$(if ($Token) {'yes'} else {'no'}))"
Start-Sleep -Seconds 20

for ($i = 1; $i -le $Clients; $i += 1) {
    $index = $i
    $clientId = $FirstJoinClientId + ($i - 1)
    $env:PWA_COOP = 'client'
    $env:PWA_COOP_INDEX = "$index"
    $env:PWA_COOP_ENDPOINT = "127.0.0.1:$BrokerPort"
    $env:PWA_COOP_SESSION = $Session
    if ($Token) { $env:PWA_COOP_TOKEN = $Token } else { Remove-Item Env:\PWA_COOP_TOKEN -ErrorAction SilentlyContinue }
    Start-Process -FilePath $exe -WorkingDirectory $GameRoot -ArgumentList @(
        '--windowed','--force-steam','off','--clientId',"$clientId",'-fastmp','join'
    ) | Out-Null
    Write-Output "[coop] client started (clientId=$clientId, index=$index)"
    Start-Sleep -Seconds 6
}

Remove-Item Env:\PWA_COOP -ErrorAction SilentlyContinue
Remove-Item Env:\PWA_COOP_INDEX -ErrorAction SilentlyContinue
Remove-Item Env:\PWA_COOP_ENDPOINT -ErrorAction SilentlyContinue
Remove-Item Env:\PWA_COOP_SESSION -ErrorAction SilentlyContinue
Remove-Item Env:\PWA_COOP_BIND -ErrorAction SilentlyContinue
Remove-Item Env:\PWA_COOP_TOKEN -ErrorAction SilentlyContinue
Start-Sleep -Seconds 12

Write-Output '[coop] ports: host=8091 / client1=8092 / client2=8093 (World prefers the first client)'
if ($Bind -ne '127.0.0.1') {
    $addresses = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notmatch '^127\.' -and $_.PrefixOrigin -ne 'WellKnown' } |
        Select-Object -ExpandProperty IPAddress
    Write-Output '[coop] remote player: install PwA mod, then'
    foreach ($address in $addresses) {
        Write-Output "  PWA_COOP=client PWA_COOP_INDEX=2 PWA_COOP_ENDPOINT=${address}:$BrokerPort PWA_COOP_TOKEN=$Token"
    }
    Write-Output '  SlayTheSpire2.exe --force-steam off --clientId 1002 -fastmp join'
}
