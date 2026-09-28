param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

Write-Host "Building DataJackUI v$Version Framework-Dependent Release..." -ForegroundColor Cyan

$PublishDir = "bin/PublishFrameworkDependent"
$ReleasesDir = "Releases"

# 1. Publish framework-dependent release
dotnet publish src/DataJackUIGui/DataJackUIGui.csproj -c Release -r win-x64 --self-contained false -o $PublishDir

# 2. Pack with Velopack
vpk pack -u DataJackUI -v $Version -p $PublishDir -e DataJackUI.exe --framework net8-x64-desktop -o $ReleasesDir

Write-Host "`nSuccessfully created Velopack release packages in '$ReleasesDir':" -ForegroundColor Green
Get-ChildItem $ReleasesDir | Select-Object Name, @{Name="SizeMB";Expression={[math]::Round($_.Length/1MB, 2)}}
