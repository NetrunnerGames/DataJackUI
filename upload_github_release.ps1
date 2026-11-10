param(
    [string]$TagName = "v2.12.3",
    [string]$ReleaseTitle = "Release v2.12.3: Entitlement Sync & Access Verification Polish",
    [string]$Owner = "NetrunnerGames",
    [string]$Repo = "DataJackUI"
)

$ErrorActionPreference = "Stop"

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host " Uploading Assets to GitHub Release $TagName via gh cli" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

# 1. Read changelog
$changelogPath = Join-Path $PSScriptRoot "changelog.txt"
$bodyText = if (Test-Path $changelogPath) { [System.IO.File]::ReadAllText($changelogPath) } else { "Release $TagName" }

# 2. Check if release already exists, or create it via gh release
$repoArg = "$Owner/$Repo"
$releaseExists = $false
try {
    $existing = & gh release view $TagName --repo $repoArg --json id 2>$null
    if ($LASTEXITCODE -eq 0 -and $existing) {
        $releaseExists = $true
        Write-Host "Found existing release for $TagName via gh" -ForegroundColor Green
    }
} catch { Write-Verbose ($_.ToString()) }

if (-not $releaseExists) {
    Write-Host "Creating new GitHub Release $TagName via gh release create..." -ForegroundColor Yellow
    & gh release create $TagName --repo $repoArg --title "$ReleaseTitle" --notes "$bodyText"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create release $TagName using gh release create."
    }
    Write-Host "Created GitHub release $TagName successfully via gh." -ForegroundColor Green
}

# 3. Resolve assets to upload
$releasesDir = Join-Path $PSScriptRoot "Releases"
$assets = @(
    "DataJackUI-Inno-Setup.exe",
    "DataJackUI-Inno-Setup.zip",
    "DataJackUI-Portable.zip",
    "RELEASES",
    "releases.win.json",
    "assets.win.json",
    "plugin.zip"
)

# Collect Velopack full and delta nupkgs
if (Test-Path $releasesDir) {
    Get-ChildItem -Path $releasesDir -Filter "DataJackUI-*-full.nupkg" | ForEach-Object {
        $assets += $_.Name
    }
    Get-ChildItem -Path $releasesDir -Filter "DataJackUI-*-delta.nupkg" | ForEach-Object {
        $assets += $_.Name
    }
}
$assets = $assets | Select-Object -Unique

# 4. Upload each asset via gh release upload --clobber
$uploadFiles = @()
foreach ($asset in $assets) {
    $assetPath = Join-Path $releasesDir $asset
    if (-not (Test-Path $assetPath)) {
        $assetPath = Join-Path $PSScriptRoot $asset
    }

    if (-not (Test-Path $assetPath)) {
        Write-Verbose "Skipping missing asset: $asset"
        continue
    }

    $uploadFiles += $assetPath
}

if ($uploadFiles.Count -gt 0) {
    Write-Host "Uploading $($uploadFiles.Count) assets to $TagName using gh release upload --clobber..." -ForegroundColor Yellow
    foreach ($file in $uploadFiles) {
        $fileName = [System.IO.Path]::GetFileName($file)
        $sizeMB = [math]::Round((Get-Item $file).Length / 1MB, 2)
        Write-Host "Uploading $fileName ($sizeMB MB)..."
        & gh release upload $TagName $file --repo $repoArg --clobber
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Successfully uploaded $fileName via gh!" -ForegroundColor Green
        } else {
            Write-Host "Warning: Failed uploading $fileName via gh release upload." -ForegroundColor Yellow
        }
    }
}

Write-Host "`n====================================================" -ForegroundColor Green
Write-Host " GitHub Release $TagName is Live with All Assets via gh cli!" -ForegroundColor Green
Write-Host " URL: https://github.com/$Owner/$Repo/releases/tag/$TagName" -ForegroundColor Green
Write-Host "====================================================" -ForegroundColor Green
