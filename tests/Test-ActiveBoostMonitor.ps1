[CmdletBinding()]
param(
    # Retained so existing build/test callers do not need to change.
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $projectRoot 'outputs\Game-Boost.ps1'
$testRoot = Join-Path $env:TEMP ('MajesticBoost-OneShot-Test-' + [Guid]::NewGuid().ToString('N'))

try {
    [void](New-Item -ItemType Directory -Path $testRoot)

    $scriptSource = [IO.File]::ReadAllText($scriptPath)
    foreach ($requiredText in @(
        '[switch]$CloseDiscord',
        '[switch]$CloseEpic',
        '[switch]$CloseSteam',
        '[switch]$CloseOneDrive',
        '[switch]$CloseTeams',
        '[switch]$CloseWallpaper',
        '[switch]$CloseNvidiaOverlay',
        '[string]$ReadySignalPath',
        '[string]$ResultPath',
        'Status=Completed'
    )) {
        if (-not $scriptSource.Contains($requiredText)) {
            throw "One-shot Active Boost contract is missing: $requiredText"
        }
    }

    foreach ($forbiddenPattern in @(
        '(?i)\bChatGPT\b',
        '(?i)\bPriorityClass\b',
        '(?i)\bProcessPriorityClass\b',
        '(?i)\bSet-BoostGamePriority\b',
        '(?i)\bHigh\b',
        '(?i)\bRealTime\b',
        '(?i)\bActiveSessionPath\b',
        '(?i)\bStart-Sleep\b'
    )) {
        if ($scriptSource -match $forbiddenPattern) {
            throw "The one-shot preparation script contains forbidden behavior: $forbiddenPattern"
        }
    }

    $defaultRequestedNames = New-Object Collections.Generic.List[string]
    $defaultReadySignalPath = Join-Path $testRoot 'default\ready.flag'
    $defaultResultPath = Join-Path $testRoot 'default\result.ini'
    & {
        param($ProductionScript, $FixtureRoot, $ReadyPath, $ReportPath, $RequestedNames)

        $env:LOCALAPPDATA = $FixtureRoot

        function Get-Process {
            [CmdletBinding()]
            param([string[]]$Name)
            foreach ($processName in $Name) {
                $RequestedNames.Add($processName)
            }
            return @()
        }

        function Get-CimInstance {
            [CmdletBinding()]
            param([string]$ClassName, [string]$Filter)
            throw 'NVIDIA discovery must not run unless CloseNvidiaOverlay is selected.'
        }

        function Start-Process {
            [CmdletBinding()]
            param(
                [string]$FilePath,
                [object[]]$ArgumentList,
                [string]$WindowStyle
            )
            throw "Unexpected process launch during fixture: $FilePath"
        }

        & $ProductionScript `
            -DoNotLaunchMajestic `
            -ReadySignalPath $ReadyPath `
            -ResultPath $ReportPath
    } $scriptPath (Join-Path $testRoot 'default') $defaultReadySignalPath $defaultResultPath $defaultRequestedNames

    if (-not (Test-Path -LiteralPath $defaultReadySignalPath)) {
        throw 'The one-shot preparation script did not publish its readiness signal.'
    }
    if (-not (Test-Path -LiteralPath $defaultResultPath)) {
        throw 'The one-shot preparation script did not publish its structured result.'
    }
    $defaultResult = [IO.File]::ReadAllText($defaultResultPath)
    foreach ($requiredResultText in @('[GameBoost]', 'FormatVersion=1', 'Status=Completed')) {
        if (-not $defaultResult.Contains($requiredResultText)) {
            throw "The structured result is missing: $requiredResultText"
        }
    }

    foreach ($requiredDefaultProcess in @(
        'SteelSeriesMoments',
        'WidgetService',
        'Widgets',
        'CrossDeviceService'
    )) {
        if (-not $defaultRequestedNames.Contains($requiredDefaultProcess)) {
            throw "The safe default preparation list is missing: $requiredDefaultProcess"
        }
    }

    foreach ($optInProcess in @(
        'Discord',
        'EpicGamesLauncher',
        'steam',
        'OneDrive',
        'ms-teams',
        'Teams',
        'wallpaper32',
        'wallpaper64',
        'NVIDIA Overlay',
        'nvsphelper64'
    )) {
        if ($defaultRequestedNames.Contains($optInProcess)) {
            throw "An opt-in process is closed by default: $optInProcess"
        }
    }

    $optInRequestedNames = New-Object Collections.Generic.List[string]
    $cimDiscoveryCount = New-Object Collections.Generic.List[int]
    $cimDiscoveryCount.Add(0)
    $optInReadySignalPath = Join-Path $testRoot 'opt-in\ready.flag'
    $optInResultPath = Join-Path $testRoot 'opt-in\result.ini'
    & {
        param($ProductionScript, $FixtureRoot, $ReadyPath, $ReportPath, $RequestedNames, $CimCounter)

        $env:LOCALAPPDATA = $FixtureRoot

        function Get-Process {
            [CmdletBinding()]
            param([string[]]$Name)
            foreach ($processName in $Name) {
                $RequestedNames.Add($processName)
            }
            return @()
        }

        function Get-CimInstance {
            [CmdletBinding()]
            param([string]$ClassName, [string]$Filter)
            $CimCounter[0] = $CimCounter[0] + 1
            return @()
        }

        function Start-Process {
            [CmdletBinding()]
            param(
                [string]$FilePath,
                [object[]]$ArgumentList,
                [string]$WindowStyle
            )
            throw "Unexpected process launch during fixture: $FilePath"
        }

        & $ProductionScript `
            -CloseDiscord `
            -CloseEpic `
            -CloseSteam `
            -CloseOneDrive `
            -CloseTeams `
            -CloseWallpaper `
            -CloseNvidiaOverlay `
            -DoNotLaunchMajestic `
            -ReadySignalPath $ReadyPath `
            -ResultPath $ReportPath
    } $scriptPath (Join-Path $testRoot 'opt-in') $optInReadySignalPath $optInResultPath $optInRequestedNames $cimDiscoveryCount

    foreach ($requiredOptInProcess in @(
        'Discord',
        'EpicGamesLauncher',
        'EpicWebHelper',
        'EpicOnlineServicesUserHelper',
        'steam',
        'steamwebhelper',
        'GameOverlayUI',
        'OneDrive',
        'ms-teams',
        'Teams',
        'wallpaper32',
        'wallpaper64',
        'NVIDIA Overlay',
        'nvsphelper64'
    )) {
        if (-not $optInRequestedNames.Contains($requiredOptInProcess)) {
            throw "An opt-in process group is missing: $requiredOptInProcess"
        }
    }
    if ($cimDiscoveryCount[0] -ne 1) {
        throw 'NVIDIA SPUser discovery must run exactly once when CloseNvidiaOverlay is selected.'
    }

    'Active Boost one-shot preparation regression test passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
