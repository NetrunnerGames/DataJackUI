param(
    [string]$Version = "",
    [string]$ChannelId = "",
    [string]$WebhookUrl = "",
    [string]$BotSecret = "",
    [string]$ChangelogText = "",
    [string]$HighlightsText = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName "System.IO.Compression.FileSystem"
Add-Type -AssemblyName "System.Net.Http"

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

# 2. Ensure Velopack installer exe exists
$ReleasesDir = "Releases"
if (-not (Test-Path $ReleasesDir)) { New-Item -ItemType Directory -Path $ReleasesDir | Out-Null }

$PublishDir = "bin/PublishFrameworkDependent"
$SetupExe = "$ReleasesDir/DataJackUI-win-Setup.exe"

if (-not (Test-Path $SetupExe)) {
    if (-not (Test-Path "$PublishDir/DataJackUI.exe")) {
        Write-Host "Publishing DataJackUI WPF Application..." -ForegroundColor Yellow
        dotnet publish src/DataJackUIGui/DataJackUIGui.csproj -c Release -r win-x64 --self-contained false -o $PublishDir
    }
    Write-Host "Packaging Velopack Installer..." -ForegroundColor Yellow
    vpk pack -u DataJackUI -v $Version -p $PublishDir -e DataJackUI.exe -i src/DataJackUIGui/icon.ico --framework net8-x64-desktop -o $ReleasesDir
}

# 3. Create Standard ZIP containing ONLY the Setup EXE (zip filename matches exe filename)
$ExeName = "DataJackUI-win-Setup.exe"
$ZipName = "DataJackUI-win-Setup.zip"
$ZipPath = "$ReleasesDir/$ZipName"
$RootZip = $ZipName

if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }
if (Test-Path $RootZip) { Remove-Item -Force $RootZip }

Write-Host "Creating Standard ZIP archive containing $ExeName..." -ForegroundColor Yellow
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "DataJackUI_Zip_$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tempDir | Out-Null
Copy-Item $SetupExe "$tempDir/$ExeName" -Force

[System.IO.Compression.ZipFile]::CreateFromDirectory($tempDir, $RootZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Copy-Item $RootZip $ZipPath -Force
Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue

$zipSizeMb = [math]::Round((Get-Item $RootZip).Length / 1MB, 2)
Write-Host "Created Standard ZIP ($zipSizeMb MB) at: $RootZip" -ForegroundColor Green

# 4. Resolve Changelog and Highlights from .txt files if not explicitly provided
$ChangelogFile = Join-Path $PSScriptRoot "changelog.txt"
$HighlightsFile = Join-Path $PSScriptRoot "highlights.txt"

if ([string]::IsNullOrWhiteSpace($ChangelogText) -and (Test-Path $ChangelogFile)) {
    $ChangelogText = [System.IO.File]::ReadAllText($ChangelogFile)
}

if ([string]::IsNullOrWhiteSpace($HighlightsText) -and (Test-Path $HighlightsFile)) {
    $HighlightsText = [System.IO.File]::ReadAllText($HighlightsFile)
}

# 5. Post Announcement via Worker
$workerUrl = "https://bots.netrunnergames.workers.dev/api/announce"
Write-Host "Dispatching release payload to $workerUrl..." -ForegroundColor Yellow

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
if ($HighlightsText) { Add-FormField $content "highlights" $HighlightsText }

if ($ChannelId) { Add-FormField $content "channel_id" $ChannelId }
if ($WebhookUrl) { Add-FormField $content "webhook_url" $WebhookUrl }

if ($BotSecret) {
    $client.DefaultRequestHeaders.Add("X-Bot-Secret", $BotSecret)
}

$fileBytes = [System.IO.File]::ReadAllBytes($RootZip)
Add-FormFile $content "file" $ZipName $fileBytes

try {
    $response = $client.PostAsync($workerUrl, $content).GetAwaiter().GetResult()
    $resText = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Write-Host "Worker Response (HTTP $($response.StatusCode)): $resText" -ForegroundColor Green
} catch {
    Write-Host "Announcement dispatch error: $_" -ForegroundColor Red
}
