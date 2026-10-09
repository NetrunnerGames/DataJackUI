param(
    [ValidateSet("patch", "minor", "major")]
    [string]$Type = "patch",

    [string]$SetVersion = ""
)

$ErrorActionPreference = "Stop"
$csprojPath = Join-Path $PSScriptRoot "src/DataJackUIGui/DataJackUIGui.csproj"

if (-not (Test-Path $csprojPath)) {
    throw "DataJackUIGui.csproj not found at $csprojPath"
}

[xml]$xml = Get-Content $csprojPath
$currentVersion = $xml.Project.PropertyGroup.Version

if ([string]::IsNullOrWhiteSpace($currentVersion)) {
    throw "Could not parse current version from $csprojPath"
}

if ([string]::IsNullOrWhiteSpace($SetVersion)) {
    $parts = $currentVersion.Split('.')
    if ($parts.Length -lt 3) {
        throw "Current version format '$currentVersion' is invalid for Semantic Versioning"
    }

    [int]$major = [int]$parts[0]
    [int]$minor = [int]$parts[1]
    [int]$patch = [int]$parts[2]

    switch ($Type) {
        "major" { $major++; $minor = 0; $patch = 0 }
        "minor" { $minor++; $patch = 0 }
        "patch" { $patch++ }
    }
    $newVersion = "$major.$minor.$patch"
} else {
    $newVersion = $SetVersion
}

$xml.Project.PropertyGroup.Version = $newVersion
$xml.Save($csprojPath)

Write-Host "DataJackUI version bumped from v$currentVersion to v$newVersion in DataJackUIGui.csproj" -ForegroundColor Green
return $newVersion
