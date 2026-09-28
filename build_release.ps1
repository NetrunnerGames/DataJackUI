param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName "System.IO.Compression.FileSystem"

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host " Building DataJackUI v$Version Release Package" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

$PublishDir = "bin/PublishFrameworkDependent"
$ReleasesDir = "Releases"

if (-not (Test-Path $ReleasesDir)) {
    New-Item -ItemType Directory -Path $ReleasesDir | Out-Null
}

# 1. Publish Framework-Dependent C# Application
Write-Host "`n1. Publishing DataJackUI WPF Application..." -ForegroundColor Yellow
dotnet publish src/DataJackUIGui/DataJackUIGui.csproj -c Release -r win-x64 --self-contained false -o $PublishDir

# 2. Pack Application with Velopack
Write-Host "`n2. Packaging Velopack Installer & Delta Updates..." -ForegroundColor Yellow
vpk pack -u DataJackUI -v $Version -p $PublishDir -e DataJackUI.exe --framework net8-x64-desktop -o $ReleasesDir

# 3. Package Plugin (Jack-in) into plugin.zip
Write-Host "`n3. Packaging DataJackUI Plugin (NetrunnerGames/Jack-in)..." -ForegroundColor Yellow
$PluginSrc = Resolve-Path "src/DataJackUIPlugin"
$ZipTarget = Join-Path (Resolve-Path . | Select-Object -ExpandProperty Path) "$ReleasesDir/plugin.zip"

if (Test-Path $ZipTarget) { Remove-Item -Force $ZipTarget }
[System.IO.Compression.ZipFile]::CreateFromDirectory($PluginSrc, $ZipTarget)

Write-Host "`n====================================================" -ForegroundColor Green
Write-Host " Successfully Created All Release Artifacts in '$ReleasesDir':" -ForegroundColor Green
Write-Host "====================================================" -ForegroundColor Green
Get-ChildItem $ReleasesDir | Select-Object Name, @{Name="SizeMB";Expression={[math]::Round($_.Length/1MB, 2)}}
