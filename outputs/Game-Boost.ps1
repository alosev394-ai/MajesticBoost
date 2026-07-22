[CmdletBinding()]
param(
    [switch]$CloseDiscord,
    [switch]$CloseEpic,
    [switch]$CloseSteam,
    [switch]$StopProxy,
    [switch]$SingleMonitor,
    [switch]$DoNotLaunchMajestic,
    [string]$ReadySignalPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Continue'
$logDirectory = Join-Path $env:LOCALAPPDATA 'MajesticBoost'
if (-not (Test-Path -LiteralPath $logDirectory)) {
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
}
$logPath = Join-Path $logDirectory 'Game-Boost.last.log'
"[$(Get-Date -Format o)] Game Boost started." | Set-Content -LiteralPath $logPath -Encoding UTF8

$processesToStop = @(
    'ChatGPT',
    'SteelSeriesMoments',
    'NVIDIA Overlay',
    'nvsphelper64',
    'wallpaper32',
    'wallpaper64',
    'WidgetService',
    'Widgets',
    'CrossDeviceService',
    'ms-teams',
    'OneDrive'
)
if ($CloseDiscord) { $processesToStop += 'Discord' }
if ($CloseEpic) { $processesToStop += @('EpicGamesLauncher', 'EpicWebHelper', 'EpicOnlineServicesUserHelper') }
if ($CloseSteam) { $processesToStop += @('steam', 'steamwebhelper', 'GameOverlayUI') }
if ($StopProxy) { $processesToStop += @('Happ', 'happd', 'xray') }
$processesToStop = @($processesToStop | Select-Object -Unique)

function Stop-BoostBackgroundProcesses {
    foreach ($processName in $processesToStop) {
        $processes = Get-Process -Name $processName -ErrorAction SilentlyContinue
        foreach ($process in $processes) {
            "Stopping $($process.Name) (PID $($process.Id))." | Add-Content -LiteralPath $logPath -Encoding UTF8
            $process | Stop-Process -Force -ErrorAction SilentlyContinue
        }
    }
}

function Set-BoostGamePriority {
    $games = Get-Process -Name 'GTA5', 'GTA5_Enhanced' -ErrorAction SilentlyContinue
    foreach ($game in $games) {
        try {
            if ($game.PriorityClass -ne 'High') {
                $game.PriorityClass = 'High'
                "Set $($game.Name) PID $($game.Id) priority to High." | Add-Content -LiteralPath $logPath -Encoding UTF8
            }
        }
        catch {
            "Could not change game priority: $($_.Exception.Message)" | Add-Content -LiteralPath $logPath -Encoding UTF8
        }
    }
    return @($games).Count
}

Stop-BoostBackgroundProcesses

Get-CimInstance Win32_Process -Filter "Name='nvcontainer.exe'" |
    Where-Object { $_.CommandLine -match '\\plugins\\SPUser' } |
    ForEach-Object {
        "Stopping NVIDIA SPUser container (PID $($_.ProcessId))." | Add-Content -LiteralPath $logPath -Encoding UTF8
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
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

$gameCount = Set-BoostGamePriority
if ($gameCount -eq 0) {
    'GTA is not running yet; the active application monitor will apply High priority when it starts.' |
        Add-Content -LiteralPath $logPath -Encoding UTF8
}
