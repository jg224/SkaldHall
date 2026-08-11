[CmdletBinding()]
param(
    [switch]$TestPackage
)

$ErrorActionPreference = 'Stop'
$workspace = $PSScriptRoot
& (Join-Path $workspace 'verify.ps1')

$iconPath = Join-Path $workspace 'thunderstore\icon.png'
if (-not $TestPackage -and -not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
    throw 'Add your 256x256 thunderstore\icon.png before creating the Thunderstore package.'
}

$manifestPath = Join-Path $workspace 'thunderstore\manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$packageName = $manifest.name
$version = $manifest.version_number
if ([string]::IsNullOrWhiteSpace($packageName) -or [string]::IsNullOrWhiteSpace($version) -or
    $packageName -notmatch '^[A-Za-z0-9_]+$') {
    throw 'Thunderstore manifest does not contain a safe package name and version_number.'
}
$packageSuffix = if ($TestPackage) { '-test' } else { '' }
$dist = Join-Path $workspace 'dist'
$stage = Join-Path $dist ("$packageName-$version$packageSuffix")
$zip = Join-Path $dist ("$packageName-$version$packageSuffix.zip")
$resolvedDist = [System.IO.Path]::GetFullPath($dist).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$resolvedStage = [System.IO.Path]::GetFullPath($stage)
$resolvedZip = [System.IO.Path]::GetFullPath($zip)
if (-not $resolvedStage.StartsWith($resolvedDist, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not $resolvedZip.StartsWith($resolvedDist, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package output escaped the project dist directory.'
}
New-Item -ItemType Directory -Path $dist -Force | Out-Null
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Path $stage | Out-Null

Copy-Item -LiteralPath (Join-Path $workspace 'src\ArenaGuard\bin\Release\net472\ArenaGuard.dll') -Destination $stage
Copy-Item -LiteralPath $manifestPath -Destination $stage
Copy-Item -LiteralPath (Join-Path $workspace 'thunderstore\README.md') -Destination (Join-Path $stage 'README.md')
Copy-Item -LiteralPath (Join-Path $workspace 'thunderstore\CHANGELOG.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $workspace 'LICENSE') -Destination $stage
if (Test-Path -LiteralPath $iconPath -PathType Leaf) {
    Copy-Item -LiteralPath $iconPath -Destination $stage
}

if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Created $zip" -ForegroundColor Green
