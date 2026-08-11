[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$workspace = $PSScriptRoot
$solution = Join-Path $workspace 'ArenaGuard.slnx'
$releaseDir = Join-Path $workspace 'src\ArenaGuard\bin\Release\net472'
$assembly = Join-Path $releaseDir 'ArenaGuard.dll'

dotnet build $solution -c Release
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

dotnet run --project (Join-Path $workspace 'tests\ArenaGuard.CoreTests\ArenaGuard.CoreTests.csproj') -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }

dotnet run --project (Join-Path $workspace 'tests\ArenaGuard.ApiTests\ArenaGuard.ApiTests.csproj') -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'API tests failed.' }

if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) {
    throw "Release DLL was not produced: $assembly"
}

$unexpected = Get-ChildItem -LiteralPath $releaseDir -File |
    Where-Object { $_.Name -notin @('ArenaGuard.dll', 'ArenaGuard.pdb') }
if ($unexpected) {
    throw "Runtime dependencies were copied into the release output: $($unexpected.Name -join ', ')"
}

$manifestPath = Join-Path $workspace 'thunderstore\manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.name -ne 'SkaldHall' -or $manifest.version_number -ne '0.0.1') {
    throw 'Thunderstore manifest name/version does not match SkaldHall 0.0.1.'
}

if ([string]::IsNullOrWhiteSpace($manifest.description) -or $manifest.description.Length -gt 250 -or
    $manifest.description -notmatch '(?i)alpha' -or $manifest.description -notmatch '(?i)quest') {
    throw 'Thunderstore description must identify the alpha and planned quest systems in 250 characters or fewer.'
}

$expectedDependencies = @(
    'denikson-BepInExPack_Valheim-5.4.2333',
    'ValheimModding-Jotunn-2.29.2'
)
if (@($manifest.dependencies).Count -ne $expectedDependencies.Count -or
    @($expectedDependencies | Where-Object { $_ -notin $manifest.dependencies }).Count -ne 0) {
    throw 'Thunderstore dependencies do not match the validated BepInExPack and Jotunn versions.'
}

$iconPath = Join-Path $workspace 'thunderstore\icon.png'
if (Test-Path -LiteralPath $iconPath -PathType Leaf) {
    Add-Type -AssemblyName System.Drawing
    $icon = [System.Drawing.Image]::FromFile($iconPath)
    try {
        if ($icon.Width -ne 256 -or $icon.Height -ne 256) {
            throw 'thunderstore\icon.png must be exactly 256x256 pixels.'
        }
        $bitmap = [System.Drawing.Bitmap]$icon
        $hasTransparency = $false
        for ($y = 0; $y -lt $bitmap.Height -and -not $hasTransparency; $y++) {
            for ($x = 0; $x -lt $bitmap.Width; $x++) {
                if ($bitmap.GetPixel($x, $y).A -lt 255) {
                    $hasTransparency = $true
                    break
                }
            }
        }
        if (-not $hasTransparency) {
            throw 'thunderstore\icon.png must contain real transparent pixels.'
        }
    }
    finally {
        $icon.Dispose()
    }
}
else {
    Write-Host 'NOTE: thunderstore\icon.png is intentionally pending; add your 256x256 icon before packaging.' -ForegroundColor Yellow
}

$packageReadme = Get-Content -LiteralPath (Join-Path $workspace 'thunderstore\README.md') -Raw
$packageChangelog = Get-Content -LiteralPath (Join-Path $workspace 'thunderstore\CHANGELOG.md') -Raw
if ($packageReadme -notmatch '(?m)^# SkaldHall\s*$' -or
    $packageReadme -notmatch '(?i)alpha' -or $packageReadme -notmatch '(?i)quest-giver') {
    throw 'Thunderstore README must use the SkaldHall name and describe the alpha and planned quest-giver.'
}
if ($packageChangelog -notmatch '(?m)^## 0\.0\.1 Alpha') {
    throw 'Thunderstore changelog must begin with the SkaldHall 0.0.1 Alpha release.'
}

Write-Host 'SkaldHall verification passed: clean Release build, 38 core tests, 15 API tests.' -ForegroundColor Green
