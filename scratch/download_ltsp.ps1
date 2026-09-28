$outDir = "scratch/ltsp_inspect"
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir | Out-Null
}

$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/madoiscool/LTSP/releases/latest"
Write-Host "Latest release tag: $($rel.tag_name)" -ForegroundColor Cyan

foreach ($asset in $rel.assets) {
    $dest = Join-Path $outDir $asset.name
    Write-Host "Downloading $($asset.name) -> $dest..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $dest
}

Write-Host "`nDownloaded assets:" -ForegroundColor Green
Get-ChildItem $outDir | Select-Object Name, Length
