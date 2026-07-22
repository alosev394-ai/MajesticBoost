[CmdletBinding()]
param(
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This UI regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\MajesticBoost.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$activateName = [Text.Encoding]::UTF8.GetString(
    [Convert]::FromBase64String('0JDQutGC0LjQstC40YDQvtCy0LDRgtGMIEJvb3N0INC/0YDQvtC40LfQstC+0LTQuNGC0LXQu9GM0L3QvtGB0YLQuA=='))
$deactivateName = [Text.Encoding]::UTF8.GetString(
    [Convert]::FromBase64String('0J7RgtC60LvRjtGH0LjRgtGMIEJvb3N0INC/0YDQvtC40LfQstC+0LTQuNGC0LXQu9GM0L3QvtGB0YLQuA=='))

function Wait-ForMainWindow {
    param([Diagnostics.Process]$Process, [int]$TimeoutMilliseconds)

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "Portable application exited unexpectedly with code $($Process.ExitCode)."
        }
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return [Windows.Automation.AutomationElement]::FromHandle($Process.MainWindowHandle)
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'Portable application window did not appear.'
}

function Wait-ForButton {
    param(
        [Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [int]$TimeoutMilliseconds
    )

    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $button = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if ($button -and $button.Current.IsEnabled) {
            return $button
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Button state did not appear: $Name"
}

function Invoke-AutomationButton {
    param([Windows.Automation.AutomationElement]$Button)

    $pattern = $Button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    ([Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Wait-ForPathState {
    param([string]$Path, [bool]$Exists, [int]$TimeoutMilliseconds)

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Test-Path -LiteralPath $Path) -eq $Exists) {
            return
        }
        Start-Sleep -Milliseconds 50
    }
    throw "Path state did not become '$Exists': $Path"
}

$process = $null
try {
    $process = Start-Process -FilePath $ApplicationPath -ArgumentList '--skip-setup', '--demo' -PassThru
    $window = Wait-ForMainWindow -Process $process -TimeoutMilliseconds 8000
    $activateButton = Wait-ForButton -Root $window -Name $activateName -TimeoutMilliseconds 20000

    $bounds = $window.Current.BoundingRectangle
    $aspectRatio = $bounds.Height / $bounds.Width
    if ($aspectRatio -lt 1.13 -or $aspectRatio -gt 1.20) {
        throw "Unexpected main-window aspect ratio: $aspectRatio ($($bounds.Width)x$($bounds.Height))."
    }

    Invoke-AutomationButton -Button $activateButton
    $deactivateButton = Wait-ForButton -Root $window -Name $deactivateName -TimeoutMilliseconds 5000
    $monitorSignal = Join-Path $env:TEMP ("MajesticBoost-demo-monitor-$($process.Id).flag")
    Wait-ForPathState -Path $monitorSignal -Exists $true -TimeoutMilliseconds 3000
    Invoke-AutomationButton -Button $deactivateButton
    [void](Wait-ForButton -Root $window -Name $activateName -TimeoutMilliseconds 5000)
    Wait-ForPathState -Path $monitorSignal -Exists $false -TimeoutMilliseconds 3000

    "Boost UI state test passed: $($bounds.Width)x$($bounds.Height), activate -> deactivate."
}
finally {
    if ($process -and -not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(3000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}
