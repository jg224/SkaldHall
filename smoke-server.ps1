param(
    [string]$ServerRoot = "C:\ValheimServer\server",
    [int]$Port = 2496,
    [switch]$AllowNetworkLaunch
)

$ErrorActionPreference = "Stop"
if (-not $AllowNetworkLaunch)
{
    Write-Output "LIVE_SERVER_SMOKE_SKIPPED=True"
    Write-Output "REASON=Routine verification does not launch valheim_server.exe or request Windows Firewall access."
    Write-Output "OPT_IN=Pass -AllowNetworkLaunch only when an interactive live-server smoke is explicitly wanted."
    return
}

$workspaceRoot = $PSScriptRoot
$sourceServerRoot = (Resolve-Path -LiteralPath $ServerRoot).Path
$sourceDll = Join-Path $workspaceRoot "src\ArenaGuard\bin\Release\net472\SkaldHall.dll"
$isolatedRoot = Join-Path $workspaceRoot ".arena-server-smoke-live"
$pluginsRoot = Join-Path $isolatedRoot "BepInEx\plugins"
$smokePlugin = Join-Path $pluginsRoot "ArenaGuardSmoke"
$smokeRun = Join-Path $isolatedRoot "save"
$stdout = Join-Path $isolatedRoot "stdout.log"
$stderr = Join-Path $isolatedRoot "stderr.log"
$logOutput = Join-Path $isolatedRoot "BepInEx\LogOutput.log"
$serverExe = Join-Path $isolatedRoot "valheim_server.exe"
$process = $null
$pluginLoaded = $false
$worldReady = $false
$skaldHallFault = $false
$signVisualValidated = $false
$challengeHostValidated = $false

try
{
    if (Test-Path -LiteralPath $isolatedRoot)
    {
        throw "Refusing to overwrite existing isolated smoke directory: $isolatedRoot"
    }
    if (-not (Test-Path -LiteralPath $sourceDll))
    {
        throw "Release DLL not found: $sourceDll"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $sourceServerRoot "valheim_server.exe")))
    {
        throw "Server executable not found under: $sourceServerRoot"
    }
    if (Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue)
    {
        throw "Smoke port $Port is already in use."
    }

    New-Item -ItemType Directory -Path $isolatedRoot | Out-Null
    foreach ($directoryName in @("valheim_server_Data", "MonoBleedingEdge", "doorstop_libs"))
    {
        New-Item -ItemType Junction `
            -Path (Join-Path $isolatedRoot $directoryName) `
            -Target (Join-Path $sourceServerRoot $directoryName) | Out-Null
    }

    foreach ($fileName in @(
        "valheim_server.exe", "UnityPlayer.dll", "UnityCrashHandler64.exe",
        "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "steam_appid.txt",
        "steamclient64.dll", "steamwebrtc64.dll", "tier0_s64.dll", "vstdlib_s64.dll"))
    {
        $sourceFile = Join-Path $sourceServerRoot $fileName
        if (Test-Path -LiteralPath $sourceFile -PathType Leaf)
        {
            Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $isolatedRoot $fileName)
        }
    }

    New-Item -ItemType Directory -Path (Join-Path $isolatedRoot "BepInEx\core") -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceServerRoot "BepInEx\core\*") `
        -Destination (Join-Path $isolatedRoot "BepInEx\core") -Recurse
    New-Item -ItemType Directory -Path (Join-Path $isolatedRoot "BepInEx\config") -Force | Out-Null
    $bepInExConfig = Join-Path $sourceServerRoot "BepInEx\config\BepInEx.cfg"
    if (Test-Path -LiteralPath $bepInExConfig -PathType Leaf)
    {
        Copy-Item -LiteralPath $bepInExConfig -Destination (Join-Path $isolatedRoot "BepInEx\config\BepInEx.cfg")
    }
    New-Item -ItemType Directory -Path $pluginsRoot -Force | Out-Null
    foreach ($dependencyName in @("Jotunn.dll", "YamlDotNet.dll"))
    {
        $dependency = Join-Path $sourceServerRoot ("BepInEx\plugins\" + $dependencyName)
        if (Test-Path -LiteralPath $dependency -PathType Leaf)
        {
            Copy-Item -LiteralPath $dependency -Destination $pluginsRoot
        }
    }
    New-Item -ItemType Directory -Path $smokePlugin | Out-Null
    Copy-Item -LiteralPath $sourceDll -Destination (Join-Path $smokePlugin "SkaldHall.dll")
    New-Item -ItemType Directory -Path $smokeRun | Out-Null

    $arguments = @(
        "-nographics", "-batchmode",
        "-name", "ArenaGuardSmoke",
        "-world", "ArenaGuardSmoke",
        "-savedir", $smokeRun,
        "-public", "0",
        "-port", $Port.ToString(),
        "-password", "ArenaSmoke123"
    )
    $process = Start-Process -FilePath $serverExe -ArgumentList $arguments `
        -WorkingDirectory $isolatedRoot -WindowStyle Hidden `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru

    $deadline = (Get-Date).AddSeconds(70)
    do
    {
        Start-Sleep -Seconds 2
        $process.Refresh()
        $consoleText = if (Test-Path -LiteralPath $stdout)
        {
            Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue
        }
        else { "" }
        $diskText = if (Test-Path -LiteralPath $logOutput)
        {
            Get-Content -LiteralPath $logOutput -Raw -ErrorAction SilentlyContinue
        }
        else { "" }
        $text = $consoleText + [Environment]::NewLine + $diskText

        $pluginLoaded = $text -match "Loading \[SkaldHall 0\.0\.2\]" -or
            $text -match "SkaldHall.*0\.0\.2"
        $worldReady = $text -match "Game server connected" -or
            $text -match "Registering lobby" -or
            $text -match "World loaded"
        $signVisualValidated =
            $text -match "Validated visible non-solid Arena Challenge Sign: renderers=[1-9]" -and
            ([regex]::Matches($text, "Validated visible non-solid").Count -ge 5)
        $challengeHostValidated =
            $text -match "Validated stationary Arena Master Dvergr: renderers=[1-9][0-9]*, colliders=[1-9][0-9]*, persistent=True, aiDisabled=True, nonSolid=True" -and
            $text -match "Registered ArenaGuard build piece 'ArenaGuard_ChallengeHost'"
        $skaldHallFault = $text -match "(?im)^\[(Error|Fatal)\s*:SkaldHall\]" -or
            $text -match "(?im)^.*(Exception|HarmonyException|TypeLoadException|MissingMethodException).*$([Environment]::NewLine)^\s*at ArenaGuard\."
    }
    while (-not $process.HasExited -and (Get-Date) -lt $deadline -and
        -not ($pluginLoaded -and $worldReady -and $signVisualValidated -and $challengeHostValidated))

    Write-Output "SMOKE_PROCESS_ID=$($process.Id)"
    Write-Output "ISOLATED_SERVER_ROOT=$isolatedRoot"
    Write-Output "PLUGIN_LOADED=$pluginLoaded"
    Write-Output "WORLD_READY=$worldReady"
    Write-Output "SIGN_VISUAL_VALIDATED=$signVisualValidated"
    Write-Output "CHALLENGE_HOST_VALIDATED=$challengeHostValidated"
    Write-Output "SKALDHALL_FAULT=$skaldHallFault"
    Write-Output "PROCESS_EXITED_EARLY=$($process.HasExited)"
    Write-Output "RELEVANT_STARTUP_LINES:"
    ($text -split "`r?`n") |
        Select-String -Pattern "SkaldHall|Validated visible non-solid|Validated stationary Arena Master|Game server connected|Registering lobby|World loaded|Exception|HarmonyException|TypeLoadException|MissingMethodException" |
        Select-Object -Last 100 |
        ForEach-Object { $_.Line }

    $errorText = if (Test-Path -LiteralPath $stderr)
    {
        Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue
    }
    else { "" }
    Write-Output "STDERR_EMPTY=$([string]::IsNullOrWhiteSpace($errorText))"
    if (-not [string]::IsNullOrWhiteSpace($errorText))
    {
        Write-Output $errorText
    }

    if (-not $pluginLoaded -or -not $worldReady -or -not $signVisualValidated -or
        -not $challengeHostValidated -or $skaldHallFault)
    {
        throw "Isolated smoke validation did not meet all success conditions."
    }
}
finally
{
    if ($null -ne $process)
    {
        $process.Refresh()
        if (-not $process.HasExited)
        {
            Stop-Process -Id $process.Id -Force
            Wait-Process -Id $process.Id -Timeout 15 -ErrorAction SilentlyContinue
        }
    }

    $resolvedRoot = [System.IO.Path]::GetFullPath($isolatedRoot).TrimEnd("\")
    $resolvedWorkspace = [System.IO.Path]::GetFullPath($workspaceRoot).TrimEnd("\")
    if (-not $resolvedRoot.StartsWith(
        $resolvedWorkspace + "\.arena-server-smoke-",
        [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Isolated smoke cleanup target validation failed."
    }
    if (Test-Path -LiteralPath $resolvedRoot)
    {
        foreach ($junctionName in @("valheim_server_Data", "MonoBleedingEdge", "doorstop_libs"))
        {
            $junction = Join-Path $resolvedRoot $junctionName
            if (Test-Path -LiteralPath $junction)
            {
                [System.IO.Directory]::Delete($junction)
            }
        }
        $cleanupError = $null
        for ($attempt = 1; $attempt -le 15 -and (Test-Path -LiteralPath $resolvedRoot); $attempt++)
        {
            try
            {
                Remove-Item -LiteralPath $resolvedRoot -Recurse -Force -ErrorAction Stop
                $cleanupError = $null
            }
            catch
            {
                $cleanupError = $_
                if ($attempt -lt 15)
                {
                    Start-Sleep -Seconds 1
                }
            }
        }
        if (Test-Path -LiteralPath $resolvedRoot)
        {
            throw $cleanupError
        }
    }

    Write-Output "ISOLATED_SERVER_REMOVED=$(-not (Test-Path -LiteralPath $isolatedRoot))"
    $serviceState = (Get-CimInstance Win32_Service -Filter "Name='ValheimServer'").State
    Write-Output "PRODUCTION_SERVICE_STATE=$serviceState"
}
