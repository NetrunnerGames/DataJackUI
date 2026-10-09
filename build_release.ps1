param(
    [Alias("b")]
    [ValidateSet("patch", "minor", "major", "none")]
    [string]$Bump = "none",

    [Alias("d")]
    [string]$DataJackVersion = "",

    [Alias("p")]
    [string]$PluginVersion = "1.0.0",

    [Alias("h", "?")]
    [switch]$Help
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName "System.IO.Compression.FileSystem"

$csprojPath = Join-Path $PSScriptRoot "src/DataJackUIGui/DataJackUIGui.csproj"

# 0. Handle version bump if requested (-Bump patch|minor|major)
if ($Bump -ne "none") {
    if (Test-Path $csprojPath) {
        [xml]$xml = Get-Content $csprojPath
        $currentVer = $xml.Project.PropertyGroup.Version
        if (-not [string]::IsNullOrWhiteSpace($currentVer)) {
            $parts = $currentVer.Split('.')
            if ($parts.Length -ge 3) {
                [int]$major = [int]$parts[0]
                [int]$minor = [int]$parts[1]
                [int]$patch = [int]$parts[2]

                switch ($Bump) {
                    "major" { $major++; $minor = 0; $patch = 0 }
                    "minor" { $minor++; $patch = 0 }
                    "patch" { $patch++ }
                }
                $DataJackVersion = "$major.$minor.$patch"
                $xml.Project.PropertyGroup.Version = $DataJackVersion
                $xml.Save($csprojPath)
                Write-Host "Bumped DataJackUI version from v$currentVer to v$DataJackVersion in DataJackUIGui.csproj" -ForegroundColor Green
            }
        }
    }
}

if ([string]::IsNullOrWhiteSpace($DataJackVersion)) {
    if (Test-Path $csprojPath) {
        [xml]$csprojXml = Get-Content $csprojPath
        $DataJackVersion = $csprojXml.Project.PropertyGroup.Version
    }
    if ([string]::IsNullOrWhiteSpace($DataJackVersion)) {
        $DataJackVersion = "2.10.3"
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

# 5. Package Standard ZIP containing ONLY the Setup EXE (zip filename matches exe filename)
Write-Host "`n4. Packaging Standard Setup EXE ZIP in Project Root..." -ForegroundColor Yellow
$SetupExePath = "$ReleasesDir/DataJackUI-win-Setup.exe"
$RootZip = Join-Path (Resolve-Path . | Select-Object -ExpandProperty Path) "DataJackUI-win-Setup.zip"
$ReleasesZip = Join-Path (Resolve-Path . | Select-Object -ExpandProperty Path) "$ReleasesDir/DataJackUI-win-Setup.zip"

if (Test-Path $RootZip) { Remove-Item -Force $RootZip }

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "DataJackUI_Zip_$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tempDir | Out-Null
Copy-Item $SetupExePath "$tempDir/DataJackUI-win-Setup.exe" -Force

[System.IO.Compression.ZipFile]::CreateFromDirectory($tempDir, $RootZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue

Write-Host "`n====================================================" -ForegroundColor Green
Write-Host " Successfully Created All Release Artifacts:" -ForegroundColor Green
Write-Host " Root Zip: $RootZip" -ForegroundColor Green
Write-Host "====================================================" -ForegroundColor Green
Get-ChildItem $ReleasesDir | Select-Object Name, @{Name="SizeMB";Expression={[math]::Round($_.Length/1MB, 2)}}
