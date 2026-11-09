param(
    [Alias("b")]
    [ValidateSet("patch", "minor", "major", "none")]
    [string]$Bump = "none",

    [Alias("d")]
    [string]$DataJackVersion = "",

    [Alias("p")]
    [string]$PluginVersion = "1.2",

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

        # Determine the true highest previous version across csproj, git tags, and existing Releases
        $highestVer = $currentVer
        try {
            $gitTags = git tag -l --sort=-v:refname
            foreach ($tag in $gitTags) {
                $cleanTag = ($tag -replace '^v','').Trim()
                if ($cleanTag -match '^\d+\.\d+\.\d+') {
                    if ([System.Version]$cleanTag -gt [System.Version]$highestVer) {
                        $highestVer = $cleanTag
                    }
                    break # tags sorted desc, first match is highest
                }
            }
        } catch { }

        $releasesDir = Join-Path $PSScriptRoot "Releases"
        if (Test-Path $releasesDir) {
            Get-ChildItem -Path $releasesDir -Filter "DataJackUI-*-full.nupkg" | ForEach-Object {
                if ($_.Name -match 'DataJackUI-(\d+\.\d+\.\d+)-full\.nupkg') {
                    $pkgVer = $Matches[1]
                    try {
                        if ([System.Version]$pkgVer -gt [System.Version]$highestVer) {
                            $highestVer = $pkgVer
                        }
                    } catch { }
                }
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($highestVer)) {
            $parts = $highestVer.Split('.')
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
                Write-Host "Bumped DataJackUI version from v$highestVer to v$DataJackVersion in DataJackUIGui.csproj" -ForegroundColor Green
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

# Ensure releases directory exists and prune older .nupkg packages, maintaining
# only the current release and 1 previous version for delta updates.
if (-not (Test-Path $ReleasesDir)) {
    New-Item -ItemType Directory -Path $ReleasesDir | Out-Null
} else {
    # Keep only the latest 2 full packages (current + 1 previous)
    $existingFullNupkgs = Get-ChildItem "$ReleasesDir/*-full.nupkg" | Sort-Object LastWriteTime -Descending
    if ($existingFullNupkgs.Count -gt 2) {
        $existingFullNupkgs | Select-Object -Skip 2 | Remove-Item -Force
    }

    # Keep only the latest 2 delta packages (current + 1 previous)
    $existingDeltaNupkgs = Get-ChildItem "$ReleasesDir/*-delta.nupkg" | Sort-Object LastWriteTime -Descending
    if ($existingDeltaNupkgs.Count -gt 2) {
        $existingDeltaNupkgs | Select-Object -Skip 2 | Remove-Item -Force
    }
    # Note: Retain previous full and delta packages and RELEASES manifest so Velopack vpk pack
    # accurately links delta diffs against the existing base release in RELEASES and releases.win.json
}

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

# 3. Pack Velopack delta packages & binary releases
Write-Host "`n2. Packaging Velopack Installer & Delta Updates..." -ForegroundColor Yellow
vpk pack -u DataJackUI -v $DataJackVersion -p $PublishDir -e DataJackUI.exe -i src/DataJackUIGui/icon.ico --framework net8-x64-desktop --runtime win-x64 -o $ReleasesDir

# 4. Compile Inno Setup Modern Dynamic Installer (wraps Velopack metadata for seamless in-app delta updates)
Write-Host "`n3. Compiling Inno Setup Modern Installer..." -ForegroundColor Yellow
$isccPath = "C:\Program Files\Inno Setup 7\ISCC.exe"
if (-not (Test-Path $isccPath)) {
    $isccCmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($isccCmd) { $isccPath = $isccCmd.Source }
}
if (Test-Path $isccPath) {
    & "$isccPath" "/DMyAppVersion=$DataJackVersion" "$PSScriptRoot/installer/DataJackUI.iss"
    Write-Host "Inno Setup modern installer created: DataJackUI-Inno-Setup.exe" -ForegroundColor Green
}

# 4. Package Plugin (Jack-in) into plugin.zip (if plugin source exists)
if (Test-Path "src/DataJackUIPlugin") {
    Write-Host "`n3. Packaging DataJackUI Plugin (v$PluginVersion for NetrunnerGames/Jack-in)..." -ForegroundColor Yellow
    $PluginSrc = Resolve-Path "src/DataJackUIPlugin"
    $ZipTarget = Join-Path (Resolve-Path . | Select-Object -ExpandProperty Path) "$ReleasesDir/plugin.zip"

    if (Test-Path $ZipTarget) { Remove-Item -Force $ZipTarget }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($PluginSrc, $ZipTarget)
}

# 5. Package Setup ZIP (Inno Setup Installer for Discord & Releases)
Write-Host "`n4. Packaging Inno Setup ZIP..." -ForegroundColor Yellow
$InnoSetupExePath = "$ReleasesDir/DataJackUI-Inno-Setup.exe"

if (Test-Path $InnoSetupExePath) {
    $InnoZipTarget = Join-Path (Resolve-Path . | Select-Object -ExpandProperty Path) "$ReleasesDir/DataJackUI-Inno-Setup.zip"
    $InnoRootZip = Join-Path (Resolve-Path . | Select-Object -ExpandProperty Path) "DataJackUI-Inno-Setup.zip"
    if (Test-Path $InnoZipTarget) { Remove-Item -Force $InnoZipTarget }
    if (Test-Path $InnoRootZip) { Remove-Item -Force $InnoRootZip }

    $tempDirInno = Join-Path ([System.IO.Path]::GetTempPath()) "DataJackUI_InnoZip_$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $tempDirInno | Out-Null
    Copy-Item $InnoSetupExePath "$tempDirInno/DataJackUI-Inno-Setup.exe" -Force
    [System.IO.Compression.ZipFile]::CreateFromDirectory($tempDirInno, $InnoRootZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Copy-Item $InnoRootZip $InnoZipTarget -Force
    Remove-Item -Recurse -Force $tempDirInno -ErrorAction SilentlyContinue
    Write-Host "Inno Setup ZIP created: $InnoZipTarget" -ForegroundColor Green
}

# 6. Package Portable ZIP from publish output
Write-Host "`n5. Packaging Portable ZIP in Releases..." -ForegroundColor Yellow
$PortableZip = "$ReleasesDir/DataJackUI-Portable.zip"
$PortableRootZip = Join-Path (Resolve-Path . | Select-Object -ExpandProperty Path) "DataJackUI-Portable.zip"

if (Test-Path $PortableZip) { Remove-Item -Force $PortableZip }
if (Test-Path $PortableRootZip) { Remove-Item -Force $PortableRootZip }

if (Test-Path $PublishDir) {
    [System.IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path $PublishDir), $PortableZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Copy-Item $PortableZip $PortableRootZip -Force
    Write-Host "Portable ZIP created: $PortableZip" -ForegroundColor Green
} else {
    Write-Host "Warning: PublishDir ($PublishDir) not found for Portable ZIP creation!" -ForegroundColor Red
}

Write-Host "`n====================================================" -ForegroundColor Green
Write-Host " Successfully Created All Release Artifacts" -ForegroundColor Green
Write-Host "====================================================" -ForegroundColor Green
Get-ChildItem $ReleasesDir | Select-Object Name, @{Name="SizeMB";Expression={[math]::Round($_.Length/1MB, 2)}}
