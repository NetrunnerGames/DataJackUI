param(
    [Alias("d")]
    [string]$DataJackVersion = "1.0.0",

    [Alias("p")]
    [string]$PluginVersion = "1.0.0",

    [Alias("h", "?")]
    [switch]$Help
)

if ($Help) {
    Write-Host @"
====================================================================
 DataJackUI Release Build & Packaging CLI
====================================================================

Usage:
  .\build_release.ps1 [-d <version>] [-p <version>] [-h]

Options:
  -d, -DataJackVersion <VERSION>  Version string for the DataJackUI application
                                  (e.g., '1.0.0'). Packages DataJackUI-win-Setup.exe
                                  and Velopack delta packages for NetrunnerGames/DataJackUI.
                                  [default: 1.0.0]

  -p, -PluginVersion   <VERSION>  Version string for the Jack-in Steam plugin
                                  (e.g., '1.0.0'). Updates plugin.json and packages
                                  plugin.zip for NetrunnerGames/Jack-in.
                                  [default: 1.0.0]

  -h, -Help                       Display this help message and exit.

Examples:
  .\build_release.ps1 -d 1.0.0 -p 1.0.0
  .\build_release.ps1 -d 1.0.1 -p 1.1.0
  .\build_release.ps1 -h
====================================================================
"@ -ForegroundColor Cyan
    exit 0
}

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName "System.IO.Compression.FileSystem"

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host " Building DataJackUI v$DataJackVersion | Plugin v$PluginVersion" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

$PublishDir = "bin/PublishFrameworkDependent"
$ReleasesDir = "Releases"
$PluginJsonFile = "src/DataJackUIPlugin/plugin.json"

if (-not (Test-Path $ReleasesDir)) {
    New-Item -ItemType Directory -Path $ReleasesDir | Out-Null
}

# 1. Update plugin.json version property
if (Test-Path $PluginJsonFile) {
    $jsonText = [System.IO.File]::ReadAllText($PluginJsonFile)
    $jsonObj = $jsonText | ConvertFrom-Json
    $jsonObj.version = $PluginVersion
    [System.IO.File]::WriteAllText($PluginJsonFile, ($jsonObj | ConvertTo-Json -Depth 5))
}

# 2. Publish Framework-Dependent C# Application
Write-Host "`n1. Publishing DataJackUI WPF Application (v$DataJackVersion)..." -ForegroundColor Yellow
dotnet publish src/DataJackUIGui/DataJackUIGui.csproj -c Release -r win-x64 --self-contained false -o $PublishDir

# 3. Pack Application with Velopack
Write-Host "`n2. Packaging Velopack Installer & Delta Updates..." -ForegroundColor Yellow
vpk pack -u DataJackUI -v $DataJackVersion -p $PublishDir -e DataJackUI.exe --framework net8-x64-desktop -o $ReleasesDir

# 4. Package Plugin (Jack-in) into plugin.zip
Write-Host "`n3. Packaging DataJackUI Plugin (v$PluginVersion for NetrunnerGames/Jack-in)..." -ForegroundColor Yellow
$PluginSrc = Resolve-Path "src/DataJackUIPlugin"
$ZipTarget = Join-Path (Resolve-Path . | Select-Object -ExpandProperty Path) "$ReleasesDir/plugin.zip"

if (Test-Path $ZipTarget) { Remove-Item -Force $ZipTarget }
[System.IO.Compression.ZipFile]::CreateFromDirectory($PluginSrc, $ZipTarget)

Write-Host "`n====================================================" -ForegroundColor Green
Write-Host " Successfully Created All Release Artifacts in '$ReleasesDir':" -ForegroundColor Green
Write-Host "====================================================" -ForegroundColor Green
Get-ChildItem $ReleasesDir | Select-Object Name, @{Name="SizeMB";Expression={[math]::Round($_.Length/1MB, 2)}}
