param(
    [Alias("d")]
    [string]$DataJackVersion = "",

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
                                  (e.g., '2.10.1'). Packages DataJackUI-win-Setup.exe
                                  and Velopack delta packages for NetrunnerGames/DataJackUI.
                                  [default: dynamically read from DataJackUIGui.csproj]

  -p, -PluginVersion   <VERSION>  Version string for the Jack-in Steam plugin
                                  (e.g., '1.0.0'). Updates plugin.json and packages
                                  plugin.zip for NetrunnerGames/Jack-in.
                                  [default: 1.0.0]

  -h, -Help                       Display this help message and exit.

Examples:
  .\build_release.ps1
  .\build_release.ps1 -d 2.10.1 -p 1.0.0
  .\build_release.ps1 -h
====================================================================
"@ -ForegroundColor Cyan
    exit 0
}

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName "System.IO.Compression.FileSystem"

if ([string]::IsNullOrWhiteSpace($DataJackVersion)) {
    $csprojPath = Join-Path $PSScriptRoot "src/DataJackUIGui/DataJackUIGui.csproj"
    if (Test-Path $csprojPath) {
        [xml]$csprojXml = Get-Content $csprojPath
        $DataJackVersion = $csprojXml.Project.PropertyGroup.Version
    }
    if ([string]::IsNullOrWhiteSpace($DataJackVersion)) {
        $DataJackVersion = "2.10.1"
    }
}

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host " Building DataJackUI v$DataJackVersion | Plugin v$PluginVersion" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

$PublishDir = "bin/PublishFrameworkDependent"
$ReleasesDir = "Releases"
$PluginJsonFile = "src/DataJackUIPlugin/plugin.json"

# Clean publish directory
if (Test-Path $PublishDir) {
    Remove-Item -Recurse -Force $PublishDir -ErrorAction SilentlyContinue
}

# Clean releases directory
if (Test-Path $ReleasesDir) {
    Remove-Item -Recurse -Force $ReleasesDir -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $ReleasesDir | Out-Null

# 1. Update plugin.json version property (if plugin source exists locally)
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
vpk pack -u DataJackUI -v $DataJackVersion -p $PublishDir -e DataJackUI.exe -i src/DataJackUIGui/icon.ico --framework net8-x64-desktop -o $ReleasesDir

# 4. Package Plugin (Jack-in) into plugin.zip (if plugin source exists)
if (Test-Path "src/DataJackUIPlugin") {
    Write-Host "`n3. Packaging DataJackUI Plugin (v$PluginVersion for NetrunnerGames/Jack-in)..." -ForegroundColor Yellow
    $PluginSrc = Resolve-Path "src/DataJackUIPlugin"
    $ZipTarget = Join-Path (Resolve-Path . | Select-Object -ExpandProperty Path) "$ReleasesDir/plugin.zip"

    if (Test-Path $ZipTarget) { Remove-Item -Force $ZipTarget }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($PluginSrc, $ZipTarget)
}

# 5. Package Standard Uncompressed ZIP in Project Root & Releases/ (100% native Windows Explorer extraction support)
Write-Host "`n4. Packaging Standard EXE ZIP in Project Root..." -ForegroundColor Yellow
$RootZip = Join-Path (Resolve-Path . | Select-Object -ExpandProperty Path) "DataJackUI-v$DataJackVersion.zip"
$ReleasesZip = Join-Path (Resolve-Path . | Select-Object -ExpandProperty Path) "$ReleasesDir/DataJackUI-v$DataJackVersion-Standard.zip"

if (Test-Path $RootZip) { Remove-Item -Force $RootZip }
if (Test-Path $ReleasesZip) { Remove-Item -Force $ReleasesZip }

[System.IO.Compression.ZipFile]::CreateFromDirectory($PublishDir, $RootZip, [System.IO.Compression.CompressionLevel]::NoCompression, $false)
Copy-Item $RootZip $ReleasesZip -Force

Write-Host "`n====================================================" -ForegroundColor Green
Write-Host " Successfully Created All Release Artifacts:" -ForegroundColor Green
Write-Host " Root Zip: $RootZip" -ForegroundColor Green
Write-Host "====================================================" -ForegroundColor Green
Get-ChildItem $ReleasesDir | Select-Object Name, @{Name="SizeMB";Expression={[math]::Round($_.Length/1MB, 2)}}
