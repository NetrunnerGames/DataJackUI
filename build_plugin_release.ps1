# ====================================================================
# DataJackUI Plugin Release Packaging Script
# Packs src/DataJackUIPlugin into Releases/plugin.zip for NetrunnerGames/Jack-in
# ====================================================================

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName "System.IO.Compression.FileSystem"

$PluginSrc = Resolve-Path "src/DataJackUIPlugin"
$OutDir = "Releases"
$ZipTarget = Join-Path (Resolve-Path . | Select-Object -ExpandProperty Path) "$OutDir/plugin.zip"

if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir | Out-Null
}

if (Test-Path $ZipTarget) {
    Remove-Item -Force $ZipTarget
}

Write-Host "Packaging DataJackUI plugin to '$ZipTarget'..." -ForegroundColor Cyan
[System.IO.Compression.ZipFile]::CreateFromDirectory($PluginSrc, $ZipTarget)

Write-Host "`nSuccessfully created plugin release package:" -ForegroundColor Green
Get-ChildItem $ZipTarget | Select-Object Name, @{Name="SizeKB";Expression={[math]::Round($_.Length/1KB, 2)}}
