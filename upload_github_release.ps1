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
$token = $env:GITHUB_TOKEN

if (-not $token) {
    $gcmCandidates = @(
        "git-credential-manager",
        "C:\Program Files\Git\mingw64\bin\git-credential-manager.exe",
        "C:\Program Files (x86)\Git\mingw64\bin\git-credential-manager.exe"
    )
    foreach ($candidate in $gcmCandidates) {
        try {
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $candidate
            $psi.Arguments = "get"
            $psi.RedirectStandardInput = $true
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $psi.UseShellExecute = $false
            $p = [System.Diagnostics.Process]::Start($psi)
            $p.StandardInput.WriteLine("protocol=https")
            $p.StandardInput.WriteLine("host=github.com")
            $p.StandardInput.WriteLine("")
            $p.StandardInput.Close()
            $gcmOut = $p.StandardOutput.ReadToEnd()
            $p.WaitForExit()
            if ($gcmOut -match 'password=(.+)') {
                $token = $Matches[1].Trim()
                break
            }
        } catch { }
    }
}

if (-not $token) {
    try {
        $ghOut = & gh auth token 2>$null
        if ($ghOut) { $token = $ghOut.Trim() }
    } catch { }
}

if (-not $token) {
    throw "Could not retrieve git credentials token for github.com from git credential manager or environment."
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

# Also include rolling window of full and delta nupkgs present in Releases so delta builds work across versions
$rollingNupkgs = Get-ChildItem "$releasesDir/*.nupkg" | Select-Object -ExpandProperty Name
$assets += $rollingNupkgs

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
