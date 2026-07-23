[CmdletBinding()]
param(
    [switch]$CloseDiscord,
    [switch]$CloseEpic,
    [switch]$CloseSteam,
    [switch]$CloseOneDrive,
    [switch]$CloseTeams,
    [switch]$CloseWallpaper,
    [switch]$CloseNvidiaOverlay,
    [switch]$StopProxy,
    [switch]$SingleMonitor,
    [switch]$DoNotLaunchMajestic,
    [string]$ReadySignalPath,
    [string]$ResultPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Continue'
$startedUtc = [DateTime]::UtcNow
$logDirectory = Join-Path $env:LOCALAPPDATA 'MajesticBoost'
if (-not (Test-Path -LiteralPath $logDirectory)) {
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
}
$logPath = Join-Path $logDirectory 'Game-Boost.last.log'
if (-not $ResultPath) {
    $ResultPath = Join-Path $logDirectory 'Game-Boost.last.result.ini'
}
"[$(Get-Date -Format o)] Game Boost started." | Set-Content -LiteralPath $logPath -Encoding UTF8

# Active Boost is a one-shot preparation pass. The application owns all
# continuous game detection and temporary process-priority changes.
$processesToStop = @(
    'SteelSeriesMoments',
    'WidgetService',
    'Widgets',
    'CrossDeviceService'
)
if ($CloseDiscord) { $processesToStop += 'Discord' }
if ($CloseEpic) { $processesToStop += @('EpicGamesLauncher', 'EpicWebHelper', 'EpicOnlineServicesUserHelper') }
if ($CloseSteam) { $processesToStop += @('steam', 'steamwebhelper', 'GameOverlayUI') }
if ($CloseOneDrive) { $processesToStop += 'OneDrive' }
if ($CloseTeams) { $processesToStop += @('ms-teams', 'Teams') }
if ($CloseWallpaper) { $processesToStop += @('wallpaper32', 'wallpaper64') }
if ($CloseNvidiaOverlay) { $processesToStop += @('NVIDIA Overlay', 'nvsphelper64') }
if ($StopProxy) { $processesToStop += @('Happ', 'happd', 'xray') }
$processesToStop = @($processesToStop | Select-Object -Unique)
$stoppedProcesses = New-Object Collections.Generic.List[string]
$warnings = New-Object Collections.Generic.List[string]

function Stop-BoostBackgroundProcesses {
    foreach ($processName in $processesToStop) {
        $processes = Get-Process -Name $processName -ErrorAction SilentlyContinue
        foreach ($process in $processes) {
            "Stopping $($process.Name) (PID $($process.Id))." | Add-Content -LiteralPath $logPath -Encoding UTF8
            try {
                $process | Stop-Process -Force -ErrorAction Stop
                $stoppedProcesses.Add("$($process.Name)|$($process.Id)")
            }
            catch {
                $message = "Could not stop $($process.Name) (PID $($process.Id)): $($_.Exception.Message)"
                $warnings.Add($message)
                $message | Add-Content -LiteralPath $logPath -Encoding UTF8
            }
        }
    }
}

Stop-BoostBackgroundProcesses

if ($CloseNvidiaOverlay) {
    Get-CimInstance Win32_Process -Filter "Name='nvcontainer.exe'" |
        Where-Object { $_.CommandLine -match '\\plugins\\SPUser' } |
        ForEach-Object {
            "Stopping NVIDIA SPUser container (PID $($_.ProcessId))." | Add-Content -LiteralPath $logPath -Encoding UTF8
            try {
                Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop
                $stoppedProcesses.Add("nvcontainer|$($_.ProcessId)")
            }
            catch {
                $message = "Could not stop NVIDIA SPUser container (PID $($_.ProcessId)): $($_.Exception.Message)"
                $warnings.Add($message)
                $message | Add-Content -LiteralPath $logPath -Encoding UTF8
            }
        }
}

if ($SingleMonitor) {
    Start-Process -FilePath "$env:WINDIR\System32\DisplaySwitch.exe" -ArgumentList '/internal' -WindowStyle Hidden
    'Requested single-monitor mode.' | Add-Content -LiteralPath $logPath -Encoding UTF8
}

$launcherPath = Join-Path $env:LOCALAPPDATA 'MajesticLauncher\Majestic Launcher.exe'
if (-not $DoNotLaunchMajestic -and (Test-Path -LiteralPath $launcherPath)) {
    if (-not (Get-Process -Name 'Majestic Launcher' -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath $launcherPath
        "Launched $launcherPath" | Add-Content -LiteralPath $logPath -Encoding UTF8
    }
}

try {
    $resultDirectory = Split-Path -Parent $ResultPath
    if ($resultDirectory -and -not (Test-Path -LiteralPath $resultDirectory)) {
        New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    }

    $resultLines = New-Object Collections.Generic.List[string]
    $resultLines.Add('[GameBoost]')
    $resultLines.Add('FormatVersion=1')
    $resultLines.Add("StartedUtc=$($startedUtc.ToString('o'))")
    $resultLines.Add("CompletedUtc=$([DateTime]::UtcNow.ToString('o'))")
    $resultLines.Add('Status=Completed')
    $resultLines.Add("StoppedProcessCount=$($stoppedProcesses.Count)")
    $resultLines.Add("WarningCount=$($warnings.Count)")
    $resultLines.Add('')
    $resultLines.Add('[StoppedProcesses]')
    for ($index = 0; $index -lt $stoppedProcesses.Count; $index++) {
        $resultLines.Add("Process$($index + 1)=$($stoppedProcesses[$index])")
    }
    $resultLines.Add('')
    $resultLines.Add('[Warnings]')
    for ($index = 0; $index -lt $warnings.Count; $index++) {
        $safeWarning = $warnings[$index] -replace '[\r\n=]', ' '
        $resultLines.Add("Warning$($index + 1)=$safeWarning")
    }

    [IO.File]::WriteAllLines(
        $ResultPath,
        $resultLines.ToArray(),
        (New-Object Text.UTF8Encoding($false))
    )
    "Boost result written to $ResultPath" | Add-Content -LiteralPath $logPath -Encoding UTF8
}
catch {
    "Could not write boost result: $($_.Exception.Message)" | Add-Content -LiteralPath $logPath -Encoding UTF8
}

if ($ReadySignalPath) {
    try {
        $signalDirectory = Split-Path -Parent $ReadySignalPath
        if ($signalDirectory -and -not (Test-Path -LiteralPath $signalDirectory)) {
            New-Item -ItemType Directory -Path $signalDirectory -Force | Out-Null
        }
        [IO.File]::WriteAllText(
            $ReadySignalPath,
            (Get-Date).ToString('o'),
            (New-Object Text.UTF8Encoding($false))
        )
        "Boost readiness signal written to $ReadySignalPath" | Add-Content -LiteralPath $logPath -Encoding UTF8
    }
    catch {
        "Could not write boost readiness signal: $($_.Exception.Message)" | Add-Content -LiteralPath $logPath -Encoding UTF8
    }
}

'One-shot Game Boost preparation completed.' | Add-Content -LiteralPath $logPath -Encoding UTF8
