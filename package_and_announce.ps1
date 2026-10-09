param(
    [string]$Version = "",
    [string]$ChannelId = "",
    [string]$WebhookUrl = "",
    [string]$BotSecret = "",
    [string]$ChangelogText = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName "System.IO.Compression.FileSystem"

# 1. Resolve version dynamically from DataJackUIGui.csproj if not specified
if ([string]::IsNullOrWhiteSpace($Version)) {
    $csprojPath = Join-Path $PSScriptRoot "src/DataJackUIGui/DataJackUIGui.csproj"
    if (Test-Path $csprojPath) {
        [xml]$csprojXml = Get-Content $csprojPath
        $Version = $csprojXml.Project.PropertyGroup.Version
    }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = "2.10.2"
    }
}

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host " Packaging Standard ZIP & Announcing v$Version" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

# 2. Publish framework-dependent output if needed
$PublishDir = "bin/PublishFrameworkDependent"
if (-not (Test-Path "$PublishDir/DataJackUI.exe")) {
    Write-Host "Publishing DataJackUI WPF Application..." -ForegroundColor Yellow
    dotnet publish src/DataJackUIGui/DataJackUIGui.csproj -c Release -r win-x64 --self-contained false -o $PublishDir
}

# 3. Create Standard ZIP (compatible with native Windows Explorer extraction, optimized <10MB for Discord)
$ReleasesDir = "Releases"
if (-not (Test-Path $ReleasesDir)) { New-Item -ItemType Directory -Path $ReleasesDir | Out-Null }

$ZipPath = "$ReleasesDir/DataJackUI-v$Version-Standard.zip"
$RootZip = "DataJackUI-v$Version.zip"

if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }
if (Test-Path $RootZip) { Remove-Item -Force $RootZip }

Write-Host "Creating Standard ZIP archive..." -ForegroundColor Yellow
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "DataJackUI_Zip_$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tempDir | Out-Null
Copy-Item "$PublishDir/*" $tempDir -Recurse -Force

[System.IO.Compression.ZipFile]::CreateFromDirectory($tempDir, $RootZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Copy-Item $RootZip $ZipPath -Force
Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue

$zipSizeMb = [math]::Round((Get-Item $RootZip).Length / 1MB, 2)
Write-Host "Created Standard ZIP ($zipSizeMb MB) at: $RootZip" -ForegroundColor Green

# 4. Post Announcement via Worker
$workerUrl = "https://bots.netrunnergames.workers.dev/api/announce"
Write-Host "Dispatching release payload to $workerUrl..." -ForegroundColor Yellow

if ([string]::IsNullOrWhiteSpace($ChangelogText)) {
    $ChangelogText = @"
+ Automatically refresh server-side entitlements on session restore and OAuth token exchange.
+ Refactored Add tab UX: selecting a game card hides search results/dropzone and displays selected game details with a top Back button.
+ Added Back to search button to seamlessly return to search results mode.
+ Forwarded mouse wheel events on listing cards directly to the main view ScrollViewer for direct mouse wheel scrolling over listings.
+ Increased game details header banner height to fit full aspect ratio without vertical cropping.
+ Rounded top-left and top-right corners of the header banner image to match card container styling.
"@
}

function Add-FormField ($multiContent, $name, $value) {
    if (-not $value) { return }
    $sc = [System.Net.Http.StringContent]::new($value)
    $sc.Headers.ContentDisposition = [System.Net.Http.Headers.ContentDispositionHeaderValue]::new("form-data")
    $sc.Headers.ContentDisposition.Name = '"' + $name + '"'
    $multiContent.Add($sc)
}

function Add-FormFile ($multiContent, $name, $fileName, $bytes) {
    $bc = [System.Net.Http.ByteArrayContent]::new($bytes)
    $bc.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("application/zip")
    $bc.Headers.ContentDisposition = [System.Net.Http.Headers.ContentDispositionHeaderValue]::new("form-data")
    $bc.Headers.ContentDisposition.Name = '"' + $name + '"'
    $bc.Headers.ContentDisposition.FileName = '"' + $fileName + '"'
    $multiContent.Add($bc)
}

$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromMinutes(2)
$content = [System.Net.Http.MultipartFormDataContent]::new("----DataJackUIBoundary$([Guid]::NewGuid().ToString('N'))")

Add-FormField $content "version" $Version
Add-FormField $content "title" "DataJackUI v$Version Released!"
Add-FormField $content "release_url" "https://github.com/NetrunnerGames/DataJackUI/releases/tag/v$Version"
Add-FormField $content "changelog" $ChangelogText

if ($ChannelId) { Add-FormField $content "channel_id" $ChannelId }
if ($WebhookUrl) { Add-FormField $content "webhook_url" $WebhookUrl }

if ($BotSecret) {
    $client.DefaultRequestHeaders.Add("X-Bot-Secret", $BotSecret)
}

$fileBytes = [System.IO.File]::ReadAllBytes($RootZip)
Add-FormFile $content "file" "DataJackUI-v$Version.zip" $fileBytes

try {
    $response = $client.PostAsync($workerUrl, $content).GetAwaiter().GetResult()
    $resText = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Write-Host "Worker Response (HTTP $($response.StatusCode)): $resText" -ForegroundColor Green
} catch {
    Write-Host "Announcement dispatch error: $_" -ForegroundColor Red
}
