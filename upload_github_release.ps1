param(
    [string]$TagName = "v2.12.3",
    [string]$ReleaseTitle = "Release v2.12.3: Entitlement Sync & Access Verification Polish",
    [string]$Owner = "NetrunnerGames",
    [string]$Repo = "DataJackUI"
)

$ErrorActionPreference = "Stop"

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host " Uploading Assets to GitHub Release $TagName via Git Credential" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

# 1. Retrieve Git Credential Token for github.com
$inputCred = @"
protocol=https
host=github.com

"@
$out = $inputCred | git credential fill

$token = ""
foreach ($line in $out) {
    $trimmed = $line.Trim()
    if ($trimmed -match '^password=(.+)$') {
        $token = $Matches[1].Trim()
        break
    }
}

if (-not $token) {
    throw "Could not retrieve git credentials token for github.com from git credential manager."
}

$headers = @{
    "Authorization" = "token $token"
    "User-Agent"    = "DataJackUI-Releaser"
    "Accept"        = "application/vnd.github.v3+json"
}

# 2. Read changelog
$changelogPath = Join-Path $PSScriptRoot "changelog.txt"
$bodyText = if (Test-Path $changelogPath) { [System.IO.File]::ReadAllText($changelogPath) } else { "Release $TagName" }

# 3. Check if release already exists, or create it
$apiUrl = "https://api.github.com/repos/$Owner/$Repo/releases"
$release = $null

try {
    $existing = Invoke-RestMethod -Uri "$apiUrl/tags/$TagName" -Headers $headers -Method Get
    Write-Host "Found existing release for $TagName (ID: $($existing.id))" -ForegroundColor Green
    $release = $existing
} catch {
    Write-Host "Creating new GitHub Release $TagName..." -ForegroundColor Yellow
    $payload = @{
        tag_name   = $TagName
        name       = $ReleaseTitle
        body       = $bodyText
        draft      = $false
        prerelease = $false
    } | ConvertTo-Json

    $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers -Method Post -Body $payload -ContentType "application/json"
    Write-Host "Created GitHub release ID: $($release.id)" -ForegroundColor Green
}

# 4. Resolve assets to upload
$releasesDir = Join-Path $PSScriptRoot "Releases"
$assets = @(
    "DataJackUI-win-Setup.exe",
    "DataJackUI-win-Setup.zip",
    "DataJackUI-win-Portable.zip",
    "RELEASES",
    "releases.win.json",
    "assets.win.json"
)

# Add any nupkg files for the version
$nupkgs = Get-ChildItem "$releasesDir/*$($TagName.TrimStart('v'))*.nupkg" | Select-Object -ExpandProperty Name
$assets += $nupkgs

# Also include previous full nupkg if present so delta builds work across versions
$prevFull = Get-ChildItem "$releasesDir/*-full.nupkg" | Select-Object -ExpandProperty Name
$assets += $prevFull

$assets = $assets | Select-Object -Unique

# 5. Upload each asset to the release upload_url
$uploadBaseUrl = $release.upload_url -replace '\{\?name,label\}', ''

foreach ($assetName in $assets) {
    $assetPath = Join-Path $releasesDir $assetName
    if (-not (Test-Path $assetPath)) {
        Write-Host "Skipping missing asset: $assetName" -ForegroundColor DarkGray
        continue
    }

    # Delete existing asset in release if name matches
    if ($release.assets) {
        $existingAsset = $release.assets | Where-Object { $_.name -eq $assetName }
        if ($existingAsset) {
            Write-Host "Deleting previous asset: $assetName..." -ForegroundColor DarkYellow
            try {
                Invoke-RestMethod -Uri $existingAsset.url -Headers $headers -Method Delete | Out-Null
            } catch {
                Write-Host "Warning deleting previous asset: $_" -ForegroundColor DarkGray
            }
        }
    }

    $fileBytes = [System.IO.File]::ReadAllBytes($assetPath)
    $sizeMb = [math]::Round($fileBytes.Length / 1MB, 2)
    Write-Host "Uploading $assetName ($sizeMb MB)..." -ForegroundColor Yellow

    $uploadUri = "$uploadBaseUrl`?name=$assetName"
    $uploadHeaders = @{
        "Authorization" = "token $token"
        "User-Agent"    = "DataJackUI-Releaser"
        "Content-Type"  = "application/octet-stream"
    }

    $uploadRes = Invoke-RestMethod -Uri $uploadUri -Headers $uploadHeaders -Method Post -Body $fileBytes
    Write-Host "Successfully uploaded $assetName!" -ForegroundColor Green
}

Write-Host "`n====================================================" -ForegroundColor Green
Write-Host " GitHub Release $TagName is Live with All Velopack Assets!" -ForegroundColor Green
Write-Host " URL: $($release.html_url)" -ForegroundColor Green
Write-Host "====================================================" -ForegroundColor Green
