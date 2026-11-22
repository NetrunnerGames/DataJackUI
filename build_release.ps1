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
            $ghReleases = & gh release list --limit 15 --json tagName 2>$null | ConvertFrom-Json
            if ($ghReleases) {
                foreach ($rel in $ghReleases) {
                    $cleanTag = ($rel.tagName -replace '^v','').Trim()
                    if ($cleanTag -match '^\d+\.\d+\.\d+') {
                        if ([System.Version]$cleanTag -gt [System.Version]$highestVer) {
                            $highestVer = $cleanTag
                        }
                        break
                    }
                }
            }
        } catch { Write-Verbose ($_.ToString()) }

        $releasesDir = Join-Path $PSScriptRoot "Releases"
        if (Test-Path $releasesDir) {
            Get-ChildItem -Path $releasesDir -Filter "DataJackUI-*-full.nupkg" | ForEach-Object {
                if ($_.Name -match 'DataJackUI-(\d+\.\d+\.\d+)-full\.nupkg') {
                    $pkgVer = $Matches[1]
                    try {
                        if ([System.Version]$pkgVer -gt [System.Version]$highestVer) {
                            $highestVer = $pkgVer
                        }
                    } catch { Write-Verbose ($_.ToString()) }
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

# Ensure releases directory exists and clean older artifacts
if (-not (Test-Path $ReleasesDir)) {
    New-Item -ItemType Directory -Path $ReleasesDir | Out-Null
} else {
    # Clean previous build artifacts from releases directory
    Get-ChildItem -Path $ReleasesDir -Include "*.nupkg", "RELEASES", "*.json", "DataJackUI-*.zip", "DataJackUI-Inno-Setup.exe" -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue
}

# 1. Update plugin.json version property (if plugin source exists locally)
if (Test-Path $PluginJsonFile) {
    $jsonText = [System.IO.File]::ReadAllText($PluginJsonFile)
    $jsonObj = $jsonText | ConvertFrom-Json
    $jsonObj.version = $PluginVersion
    [System.IO.File]::WriteAllText($PluginJsonFile, ($jsonObj | ConvertTo-Json -Depth 5))
}

# 1.5 Code Quality, UI Styling, Security & Secret Scans
Write-Host "`n[Analysis] Formatting code with dotnet format..." -ForegroundColor Cyan
dotnet format whitespace src/DataJackUIGui/DataJackUIGui.csproj

$xstylerCmd = Get-Command xstyler.exe -ErrorAction SilentlyContinue
if ($xstylerCmd) {
    Write-Host "[Analysis] Formatting XAML files with XamlStyler..." -ForegroundColor Cyan
    & xstyler -d src/DataJackUIGui -r
} else {
    Write-Warning "xstyler CLI not found. Install globally with: dotnet tool install -g XamlStyler.Console"
}

$gitleaksCmd = Get-Command gitleaks.exe -ErrorAction SilentlyContinue
if ($gitleaksCmd) {
    Write-Host "[Analysis] Scanning for secret leaks with Gitleaks CLI..." -ForegroundColor Cyan
    & gitleaks detect --no-banner
    if ($LASTEXITCODE -ne 0) {
        throw "Gitleaks detected secret or sensitive token leaks! Aborting release build."
    }
} else {
    Write-Warning "Gitleaks CLI not detected in PATH. Skipping secrets scan."
}

$semgrepCmd = Get-Command semgrep.exe -ErrorAction SilentlyContinue
if (-not $semgrepCmd) {
    $userScripts = Join-Path ([System.Environment]::GetFolderPath('LocalApplicationData')) 'Programs/Python/Python311/Scripts'
    if (Test-Path (Join-Path $userScripts 'semgrep.exe')) {
        $env:Path = "$userScripts;" + $env:Path
        $semgrepCmd = Get-Command semgrep.exe -ErrorAction SilentlyContinue
    }
}
if ($semgrepCmd) {
    Write-Host "[Analysis] Scanning for vulnerabilities with Semgrep CLI..." -ForegroundColor Cyan
    & semgrep scan --config "p/default" --config "p/csharp" --metrics=off --error
    if ($LASTEXITCODE -ne 0) {
        throw "Semgrep security scan found blocking vulnerabilities. Aborting release build."
    }
} else {
    Write-Warning "Semgrep CLI not detected in PATH. Skipping SAST vulnerability scan."
}

# 2. Publish Framework-Dependent C# Application
Write-Host "`n1. Publishing DataJackUI WPF Application (v$DataJackVersion)..." -ForegroundColor Yellow
dotnet publish src/DataJackUIGui/DataJackUIGui.csproj -c Release -r win-x64 --self-contained false -o $PublishDir

# 3. Compile Inno Setup Modern Dynamic Installer
Write-Host "`n2. Compiling Inno Setup Modern Installer..." -ForegroundColor Yellow
$isccPath = "C:\Program Files\Inno Setup 7\ISCC.exe"
if (-not (Test-Path $isccPath)) {
    $isccCmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($isccCmd) { $isccPath = $isccCmd.Source }
}
if (Test-Path $isccPath) {
    & "$isccPath" "/DMyAppVersion=$DataJackVersion" "$PSScriptRoot/installer/DataJackUI.iss"
    Write-Host "Inno Setup modern installer created: DataJackUI-Inno-Setup.exe" -ForegroundColor Green
}

# 4. Package Setup ZIP (Inno Setup Installer for Discord & Releases)
Write-Host "`n3. Packaging Inno Setup ZIP..." -ForegroundColor Yellow
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

# 5.5 Package Direct Update ZIP containing published application binaries
Write-Host "`n5. Packaging Direct Update ZIP (DataJackUI-Update.zip)..." -ForegroundColor Yellow
$UpdateZip = "$ReleasesDir/DataJackUI-Update.zip"
$UpdateRootZip = Join-Path (Resolve-Path . | Select-Object -ExpandProperty Path) "DataJackUI-Update.zip"
if (Test-Path $UpdateZip) { Remove-Item -Force $UpdateZip }
if (Test-Path $UpdateRootZip) { Remove-Item -Force $UpdateRootZip }

if (Test-Path $PublishDir) {
    # Include DataJackUI application binaries and dependencies for direct fast in-app updating
    $tempDirUpdate = Join-Path ([System.IO.Path]::GetTempPath()) "DataJackUI_UpdateZip_$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $tempDirUpdate | Out-Null
    Copy-Item "$PublishDir/DataJackUI.*" "$tempDirUpdate" -Force
    if (Test-Path "$PublishDir/Resources") {
        Copy-Item "$PublishDir/Resources" "$tempDirUpdate/Resources" -Recurse -Force
    }
    if (Test-Path "$PublishDir/public") {
        Copy-Item "$PublishDir/public" "$tempDirUpdate/public" -Recurse -Force
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($tempDirUpdate, $UpdateRootZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Copy-Item $UpdateRootZip $UpdateZip -Force
    Remove-Item -Recurse -Force $tempDirUpdate -ErrorAction SilentlyContinue
    Write-Host "Direct Update ZIP created: $UpdateZip" -ForegroundColor Green
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
