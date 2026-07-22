[CmdletBinding()]
param(
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\MajesticBoost.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
$scriptPath = Join-Path $projectRoot 'outputs\Game-Boost.ps1'
$programPath = Join-Path $projectRoot 'MajesticBoost\Program.cs'
$testRoot = Join-Path $env:TEMP ('MajesticBoost-ActiveMonitor-Test-' + [Guid]::NewGuid().ToString('N'))
$readySignalPath = Join-Path $testRoot 'ready.flag'

try {
    [void](New-Item -ItemType Directory -Path $testRoot)

    $programSource = [IO.File]::ReadAllText($programPath)
    foreach ($requiredText in @(
        'activeBoostTimer.Interval = TimeSpan.FromSeconds(30)',
        'StartActiveBoostMaintenance()',
        'StopActiveBoostMaintenance()',
        'keepDiscordToggle.IsChecked != true',
        'keepEpicToggle.IsChecked != true',
        'keepSteamToggle.IsChecked != true',
        'Process.GetProcessesByName(gameName)',
        'ProcessPriorityClass.High',
        'MakeText(GetApplicationVersion()',
        'Assembly.GetExecutingAssembly().GetName().Version'
    )) {
        if (-not $programSource.Contains($requiredText)) {
            throw "Active Boost policy is missing: $requiredText"
        }
    }
    if ($programSource.Contains('ActiveSessionPath')) {
        throw 'The application still contains the retired external active-session monitor.'
    }
    if ($programSource.Contains('GetMajesticLauncherVersion')) {
        throw 'The chrome still reads the Majestic Launcher version instead of the application version.'
    }

    & {
        param($ProductionScript, $FixtureRoot, $ReadyPath)

        $env:LOCALAPPDATA = $FixtureRoot

        function Get-Process {
            [CmdletBinding()]
            param([string[]]$Name)
            return @()
        }

        function Get-CimInstance {
            [CmdletBinding()]
            param([string]$ClassName, [string]$Filter)
            return @()
        }

        & $ProductionScript -DoNotLaunchMajestic -ReadySignalPath $ReadyPath
    } $scriptPath $testRoot $readySignalPath

    if (-not (Test-Path -LiteralPath $readySignalPath)) {
        throw 'The one-shot preparation script did not publish its readiness signal.'
    }

    $scriptSource = [IO.File]::ReadAllText($scriptPath)
    if ($scriptSource.Contains('ActiveSessionPath') -or $scriptSource.Contains('Start-Sleep')) {
        throw 'The one-shot preparation script still contains a persistent polling monitor.'
    }

    $child = $null
    $childId = $null
    try {
        $child = Start-Process `
            -FilePath (Join-Path $env:WINDIR 'System32\ping.exe') `
            -ArgumentList '-t', '127.0.0.1' `
            -WindowStyle Hidden `
            -PassThru
        $childId = $child.Id
        $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ApplicationPath))
        $windowType = $assembly.GetType('MajesticBoost.BoostWindow', $true, $false)
        $window = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($windowType)
        $flags = [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::NonPublic
        $processField = $windowType.GetField('boostProcess', $flags)
        $stopMethod = $windowType.GetMethod('StopBoostProcess', $flags)
        $processField.SetValue($window, $child)
        [void]$stopMethod.Invoke($window, @())
        $deadline = [DateTime]::UtcNow.AddSeconds(3)
        while ([DateTime]::UtcNow -lt $deadline -and (Get-Process -Id $childId -ErrorAction SilentlyContinue)) {
            Start-Sleep -Milliseconds 50
        }
        if (Get-Process -Id $childId -ErrorAction SilentlyContinue) {
            throw 'StopBoostProcess did not terminate the exact tracked child process.'
        }
        if ($processField.GetValue($window) -ne $null) {
            throw 'StopBoostProcess did not clear the tracked child reference.'
        }
    }
    finally {
        if ($childId -and (Get-Process -Id $childId -ErrorAction SilentlyContinue)) {
            Stop-Process -Id $childId -Force -ErrorAction SilentlyContinue
        }
    }

    'Active Boost in-process monitor regression test passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
