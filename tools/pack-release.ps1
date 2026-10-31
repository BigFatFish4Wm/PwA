# 打包发布产物：release/PwABridge-<ver>.zip 与 release/cortico-world-pwa-<ver>.zip
# 用法: powershell -ExecutionPolicy Bypass -File tools\pack-release.ps1 -Version 1.0.0
param(
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$releaseDir = Join-Path $root 'release'
$stageDir = Join-Path $releaseDir '.staging'

Write-Output "[pack] version $Version"
Push-Location (Join-Path $root 'PwA')
try {
    dotnet build PwA.csproj -c Release -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }
} finally {
    Pop-Location
}

$dll = Join-Path $root 'PwA\bin\Release\net9.0\PwABridge.dll'
if (-not (Test-Path $dll)) { throw "build output missing: $dll" }

if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

# ---- Mod 包：解压后得到 mods\PwABridge\...
$modStage = Join-Path $stageDir 'PwABridge'
New-Item -ItemType Directory -Force -Path $modStage | Out-Null
Copy-Item $dll (Join-Path $modStage 'PwABridge.dll')
Copy-Item (Join-Path $root 'PwA\mod_manifest.json') (Join-Path $modStage 'PwABridge.json')
Copy-Item (Join-Path $root 'THIRD_PARTY_NOTICES.md') $modStage
Copy-Item (Join-Path $root 'README.md') $modStage
Copy-Item (Join-Path $root 'LICENSE') $modStage
Copy-Item (Join-Path $root 'tools\start-coop.ps1') $modStage
Copy-Item (Join-Path $root 'tools\sync-workshop-mods.ps1') $modStage
$modDocs = Join-Path $modStage 'docs'
New-Item -ItemType Directory -Force -Path $modDocs | Out-Null
Get-ChildItem (Join-Path $root 'docs') -Filter '*.md' | Copy-Item -Destination $modDocs

$modZip = Join-Path $releaseDir ("PwABridge-{0}.zip" -f $Version)
if (Test-Path $modZip) { Remove-Item $modZip -Force }
Compress-Archive -Path $modStage -DestinationPath $modZip
Write-Output ("[pack] {0} ({1:N0} bytes)" -f $modZip, (Get-Item $modZip).Length)

# ---- World 包：解压后得到 cortico-world-pwa\...
$worldSrc = Join-Path $root 'cortico-world-pwa'
$worldStage = Join-Path $stageDir 'cortico-world-pwa'
New-Item -ItemType Directory -Force -Path (Join-Path $worldStage 'dist') | Out-Null
Copy-Item (Join-Path $worldSrc 'package.json') $worldStage
Copy-Item (Join-Path $worldSrc 'index.js') $worldStage
Copy-Item (Join-Path $worldSrc 'ENV_PROMPT.md') $worldStage
Copy-Item (Join-Path $worldSrc 'README.md') $worldStage
Copy-Item (Join-Path $worldSrc 'dist\webui.js') (Join-Path $worldStage 'dist')
Copy-Item (Join-Path $root 'LICENSE') $worldStage
Copy-Item (Join-Path $root 'THIRD_PARTY_NOTICES.md') $worldStage

$worldZip = Join-Path $releaseDir ("cortico-world-pwa-{0}.zip" -f $Version)
if (Test-Path $worldZip) { Remove-Item $worldZip -Force }
Compress-Archive -Path $worldStage -DestinationPath $worldZip
Write-Output ("[pack] {0} ({1:N0} bytes)" -f $worldZip, (Get-Item $worldZip).Length)

Remove-Item $stageDir -Recurse -Force

Write-Output '[pack] contents:'
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($zip in @($modZip, $worldZip)) {
    Write-Output ("  {0}" -f (Split-Path -Leaf $zip))
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries) { Write-Output ("    " + $entry.FullName) }
    } finally {
        $archive.Dispose()
    }
}
