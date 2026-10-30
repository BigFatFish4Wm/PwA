param(
    [string]$Coopanion = "D:\Program Files\coopanion",
    [string]$Workspace = "D:\workspace\deepseek\new10-30",
    [string]$Name = "cortico-world-pwa"
)

# Link a world package into Coopanion's extensions/node_modules.
#   -Name cortico-world-memory   links the memory world instead of PwA.
# ASCII only: Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI.
$ErrorActionPreference = "Stop"
$target = Join-Path $Workspace $Name
$link = Join-Path $Coopanion "data\extensions\node_modules\$Name"

if (-not (Test-Path $target)) { throw "world package not found: $target" }
if (Test-Path $link) {
    $item = Get-Item $link -Force
    if ($item.LinkType) { $item.Delete() } else { Remove-Item -Recurse -Force $link }
}
New-Item -ItemType Junction -Path $link -Target $target | Out-Null
Write-Host "[link-world] linked $link -> $target"
Get-Item $link -Force | Select-Object FullName, LinkType, Target | Format-List
Write-Host "[link-world] restart Coopanion to load the world."
