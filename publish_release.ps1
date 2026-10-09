param(
    [ValidateSet("patch", "minor", "major", "none")]
    [string]$Bump = "patch",

    [string]$Title = "",
    [string]$CommitMessage = "",
    [string]$ChangelogText = "",
    [string]$HighlightsText = "",
    [string]$ChannelId = "",
    [string]$WebhookUrl = "",
    [string]$BotSecret = ""
)

$ErrorActionPreference = "Stop"

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host " Automated Release Pipeline: Build -> Git -> GitHub -> Discord" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

# 1. Build and optionally bump version
& "$PSScriptRoot/build_release.ps1" -Bump $Bump

# 2. Read final version from csproj and resolve changelog/highlights text files
$csprojPath = Join-Path $PSScriptRoot "src/DataJackUIGui/DataJackUIGui.csproj"
[xml]$csprojXml = Get-Content $csprojPath
$Version = $csprojXml.Project.PropertyGroup.Version

$ChangelogFile = Join-Path $PSScriptRoot "changelog.txt"
$HighlightsFile = Join-Path $PSScriptRoot "highlights.txt"

if ([string]::IsNullOrWhiteSpace($ChangelogText) -and (Test-Path $ChangelogFile)) {
    $ChangelogText = [System.IO.File]::ReadAllText($ChangelogFile)
}

if ([string]::IsNullOrWhiteSpace($HighlightsText) -and (Test-Path $HighlightsFile)) {
    $HighlightsText = [System.IO.File]::ReadAllText($HighlightsFile)
}

if ([string]::IsNullOrWhiteSpace($Title)) {
    $Title = "DataJackUI Update"
}
if ([string]::IsNullOrWhiteSpace($CommitMessage)) {
    $CommitMessage = "release: v$Version - $Title"
}

# 3. Git Commit, Tag & Push
Write-Host "`n1. Committing, tagging, and pushing to GitHub..." -ForegroundColor Yellow
git add -A
git commit -m "$CommitMessage"
git tag -a "v$Version" -m "Release v$Version"
git push origin main --tags

# 4. GitHub Release via gh CLI (best-effort)
Write-Host "`n2. Publishing GitHub Release v$Version..." -ForegroundColor Yellow
$releaseNotes = if ($ChangelogText) { $ChangelogText } else { "[+] Release v$Version" }
$releaseTitle = "Release v$($Version): $Title"

try {
    $setupExe = "$PSScriptRoot/Releases/DataJackUI-win-Setup.exe"
    $portableZip = "$PSScriptRoot/Releases/DataJackUI-win-Portable.zip"
    $nupkg = "$PSScriptRoot/Releases/DataJackUI-$Version-full.nupkg"
    $releasesFile = "$PSScriptRoot/Releases/RELEASES"
    $pluginZip = "$PSScriptRoot/Releases/plugin.zip"

    gh release create "v$Version" --title "$releaseTitle" --notes "$releaseNotes" $setupExe $portableZip $nupkg $releasesFile $pluginZip
    Write-Host "GitHub release v$Version created successfully." -ForegroundColor Green
} catch {
    Write-Host "Note: gh CLI release creation skipped or warning: $_" -ForegroundColor Yellow
}

# 5. Discord Announcement
Write-Host "`n3. Dispatching Discord Release Announcement..." -ForegroundColor Yellow
$announceArgs = @{
    Version        = $Version
    ChannelId      = $ChannelId
    WebhookUrl     = $WebhookUrl
    BotSecret      = $BotSecret
    ChangelogText  = $ChangelogText
    HighlightsText = $HighlightsText
}
& "$PSScriptRoot/package_and_announce.ps1" @announceArgs

Write-Host "`n====================================================" -ForegroundColor Green
Write-Host " Release v$Version Complete and Published!" -ForegroundColor Green
Write-Host "====================================================" -ForegroundColor Green
