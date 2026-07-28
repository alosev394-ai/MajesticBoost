[CmdletBinding()]
param(
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This UI regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\MajesticBoost.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$compiledAssembly = [Reflection.Assembly]::LoadFile($ApplicationPath)
$overlayType = $compiledAssembly.GetType(
    'MajesticBoost.BoostCenterOverlay',
    $true)
$bindingFlags = [Reflection.BindingFlags]::NonPublic -bor
    [Reflection.BindingFlags]::Static
$transitionField = $overlayType.GetField(
    'PageTransitionTotalMilliseconds',
    $bindingFlags)
if (-not $transitionField) {
    throw 'Compiled Boost Center does not expose its transition duration.'
}
$transitionMilliseconds = [int]$transitionField.GetRawConstantValue()
if ($transitionMilliseconds -lt 220 -or $transitionMilliseconds -gt 260) {
    throw "Compiled page transition is $transitionMilliseconds ms; expected 220..260 ms."
}

$windowType = $compiledAssembly.GetType('MajesticBoost.BoostWindow', $true)
$placementMethod = $windowType.GetMethod(
    'CalculateMonitorPlacement',
    [Reflection.BindingFlags]::NonPublic -bor
        [Reflection.BindingFlags]::Static)
if (-not $placementMethod) {
    throw 'Compiled app does not expose the pure per-monitor placement calculation.'
}

function Invoke-MonitorPlacement {
    param(
        [int]$WorkLeft,
        [int]$WorkTop,
        [int]$WorkRight,
        [int]$WorkBottom,
        [uint32]$DpiX,
        [uint32]$DpiY,
        [int]$CurrentLeft,
        [int]$CurrentTop,
        [bool]$Center
    )
    return [int[]]$placementMethod.Invoke(
        $null,
        [object[]]@(
            $WorkLeft,
            $WorkTop,
            $WorkRight,
            $WorkBottom,
            $DpiX,
            $DpiY,
            $CurrentLeft,
            $CurrentTop,
            $Center))
}

$mixedDpi = Invoke-MonitorPlacement `
    -WorkLeft -2560 `
    -WorkTop 0 `
    -WorkRight 0 `
    -WorkBottom 1440 `
    -DpiX 144 `
    -DpiY 144 `
    -CurrentLeft 0 `
    -CurrentTop 0 `
    -Center $true
if ($mixedDpi[0] -ne -1625 -or
    $mixedDpi[1] -ne 306 -or
    $mixedDpi[2] -ne 690 -or
    $mixedDpi[3] -ne 828) {
    throw "150% negative-coordinate monitor placement was incorrect: $($mixedDpi -join ', ')."
}

$standardDpi = Invoke-MonitorPlacement `
    -WorkLeft 0 `
    -WorkTop 0 `
    -WorkRight 1920 `
    -WorkBottom 1080 `
    -DpiX 96 `
    -DpiY 96 `
    -CurrentLeft 0 `
    -CurrentTop 0 `
    -Center $true
if ($standardDpi[0] -ne 730 -or
    $standardDpi[1] -ne 264 -or
    $standardDpi[2] -ne 460 -or
    $standardDpi[3] -ne 552) {
    throw "96 DPI monitor placement was incorrect: $($standardDpi -join ', ')."
}

$highDpi = Invoke-MonitorPlacement `
    -WorkLeft 0 `
    -WorkTop 0 `
    -WorkRight 2560 `
    -WorkBottom 1440 `
    -DpiX 192 `
    -DpiY 192 `
    -CurrentLeft 0 `
    -CurrentTop 0 `
    -Center $true
if ($highDpi[0] -ne 820 -or
    $highDpi[1] -ne 168 -or
    $highDpi[2] -ne 920 -or
    $highDpi[3] -ne 1104) {
    throw "200% monitor placement was incorrect: $($highDpi -join ', ')."
}

$constrainedDpi = Invoke-MonitorPlacement `
    -WorkLeft 1920 `
    -WorkTop 0 `
    -WorkRight 2520 `
    -WorkBottom 700 `
    -DpiX 144 `
    -DpiY 144 `
    -CurrentLeft 5000 `
    -CurrentTop -100 `
    -Center $false
if ($constrainedDpi[0] -ne 1945 -or
    $constrainedDpi[1] -ne 12 -or
    $constrainedDpi[2] -ne 563 -or
    $constrainedDpi[3] -ne 676) {
    throw "Constrained 150% monitor placement was incorrect: $($constrainedDpi -join ', ')."
}

$tinyWorkArea = Invoke-MonitorPlacement `
    -WorkLeft -300 `
    -WorkTop 100 `
    -WorkRight 0 `
    -WorkBottom 300 `
    -DpiX 192 `
    -DpiY 192 `
    -CurrentLeft 9999 `
    -CurrentTop -999 `
    -Center $false
if ($tinyWorkArea[0] -ne -156 -or
    $tinyWorkArea[1] -ne 116 -or
    $tinyWorkArea[2] -ne 140 -or
    $tinyWorkArea[3] -ne 168) {
    throw "Tiny 200% work-area clamping was incorrect: $($tinyWorkArea -join ', ')."
}

$fallbackDpi = Invoke-MonitorPlacement `
    -WorkLeft 0 `
    -WorkTop 0 `
    -WorkRight 1920 `
    -WorkBottom 1080 `
    -DpiX 0 `
    -DpiY 900 `
    -CurrentLeft 0 `
    -CurrentTop 0 `
    -Center $true
if ($fallbackDpi[2] -ne 460 -or
    $fallbackDpi[3] -ne 552) {
    throw 'Invalid monitor DPI did not safely fall back to 96 DPI.'
}

function Wait-ForWindow {
    param(
        [Diagnostics.Process]$Process,
        [int]$TimeoutMilliseconds
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "Majestic Boost exited unexpectedly with code $($Process.ExitCode)."
        }
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return [Windows.Automation.AutomationElement]::FromHandle(
                $Process.MainWindowHandle)
        }
        Start-Sleep -Milliseconds 50
    }
    throw 'Majestic Boost did not expose a top-level window.'
}

function Find-ById {
    param(
        [Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [int]$TimeoutMilliseconds = 10000,
        [switch]$AllowDisabled
    )

    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $element = $Root.FindFirst(
            [Windows.Automation.TreeScope]::Descendants,
            $condition)
        if ($element -and
            ($AllowDisabled -or $element.Current.IsEnabled) -and
            -not $element.Current.IsOffscreen) {
            return $element
        }
        Start-Sleep -Milliseconds 25
    }
    throw "UI element did not appear: $AutomationId"
}

function Invoke-Element {
    param([Windows.Automation.AutomationElement]$Element)

    $pattern = $Element.GetCurrentPattern(
        [Windows.Automation.InvokePattern]::Pattern)
    ([Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Get-NormalizedBounds {
    param(
        [Windows.Automation.AutomationElement]$Element,
        [Windows.Automation.AutomationElement]$Window
    )

    $windowBounds = $Window.Current.BoundingRectangle
    $bounds = $Element.Current.BoundingRectangle
    $factor = 460.0 / $windowBounds.Width
    return [pscustomobject]@{
        Left = ($bounds.Left - $windowBounds.Left) * $factor
        Top = ($bounds.Top - $windowBounds.Top) * $factor
        Width = $bounds.Width * $factor
        Height = $bounds.Height * $factor
        RightInset = ($windowBounds.Right - $bounds.Right) * $factor
        BottomInset = ($windowBounds.Bottom - $bounds.Bottom) * $factor
    }
}

function Assert-Between {
    param(
        [double]$Actual,
        [double]$Minimum,
        [double]$Maximum,
        [string]$Description
    )

    if ($Actual -lt $Minimum -or $Actual -gt $Maximum) {
        throw "$Description was $Actual; expected $Minimum..$Maximum DIP."
    }
}

function Assert-KeyboardFocusable {
    param(
        [Windows.Automation.AutomationElement]$Element,
        [string]$Description
    )

    if (-not $Element.Current.IsKeyboardFocusable) {
        throw "$Description is missing keyboard focus support."
    }
    if ([string]::IsNullOrWhiteSpace($Element.Current.Name)) {
        throw "$Description is missing an accessible name."
    }
}

function Wait-ForFocusedId {
    param(
        [string]$AutomationId,
        [int]$TimeoutMilliseconds
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $focused = [Windows.Automation.AutomationElement]::FocusedElement
        if ($focused -and
            $focused.Current.AutomationId -ceq $AutomationId) {
            return
        }
        Start-Sleep -Milliseconds 10
    }
    $actual = [Windows.Automation.AutomationElement]::FocusedElement
    $actualId = if ($actual) { $actual.Current.AutomationId } else { '<none>' }
    throw "Keyboard focus was '$actualId', expected '$AutomationId'."
}

$scales = @(1.0, 1.25, 1.5, 2.0)
$results = @()
foreach ($scale in $scales) {
    $process = $null
    try {
        $scaleArgument = '--demo-ui-scale=' +
            $scale.ToString([Globalization.CultureInfo]::InvariantCulture)
        $process = Start-Process `
            -FilePath $ApplicationPath `
            -ArgumentList '--skip-setup', '--demo', $scaleArgument `
            -PassThru
        $window = Wait-ForWindow -Process $process -TimeoutMilliseconds 15000

        $windowBounds = $window.Current.BoundingRectangle
        $aspect = $windowBounds.Height / $windowBounds.Width
        Assert-Between $aspect 1.18 1.22 "Window aspect at $scale scale"

        $gear = Find-ById $window 'MajesticBoost.OpenCenter' 15000
        $version = Find-ById $window 'MajesticBoost.Version'
        $minimize = Find-ById $window 'MajesticBoost.Minimize'
        $close = Find-ById $window 'MajesticBoost.Close'
        $boost = Find-ById $window 'MajesticBoost.Activate' 20000
        $discord = Find-ById $window 'MajesticBoost.Keep.DISCORD'
        $epic = Find-ById $window 'MajesticBoost.Keep.EPICGAMES'
        $steam = Find-ById $window 'MajesticBoost.Keep.STEAM'
        $watermark = Find-ById $window 'MajesticBoost.Watermark' 10000 -AllowDisabled

        foreach ($pair in @(
            @($gear, 'Settings button'),
            @($boost, 'Boost button'),
            @($discord, 'Discord switch'),
            @($epic, 'Epic Games switch'),
            @($steam, 'Steam switch'),
            @($minimize, 'Minimize button'),
            @($close, 'Close button'))) {
            Assert-KeyboardFocusable $pair[0] $pair[1]
        }

        foreach ($chrome in @($gear, $minimize, $close)) {
            $chromeBounds = Get-NormalizedBounds $chrome $window
            Assert-Between $chromeBounds.Width 31 33 'Title control width'
            Assert-Between $chromeBounds.Height 31 33 'Title control height'
            Assert-Between $chromeBounds.Top 8.5 13.5 'Title control top inset'
        }

        $gearBounds = Get-NormalizedBounds $gear $window
        $closeBounds = Get-NormalizedBounds $close $window
        Assert-Between $gearBounds.Left 8.5 13.5 'Settings button left inset'
        Assert-Between $closeBounds.RightInset 8.5 13.5 'Close button right inset'

        $versionBounds = Get-NormalizedBounds $version $window
        $minimizeBounds = Get-NormalizedBounds $minimize $window
        if ($versionBounds.Left + $versionBounds.Width -gt
            $minimizeBounds.Left + 0.5) {
            throw 'Version text overlaps the minimize button.'
        }

        foreach ($switch in @($discord, $epic, $steam)) {
            $switchBounds = Get-NormalizedBounds $switch $window
            Assert-Between $switchBounds.Height 37 39 'Main switch row height'
            if ($switchBounds.RightInset -lt 24) {
                throw "Main switch entered the 24 DIP right safe area at scale $scale."
            }
        }
        $steamBounds = Get-NormalizedBounds $steam $window
        if ($steamBounds.BottomInset -lt 24) {
            throw "Main switch group has less than 24 DIP bottom breathing room."
        }
        $watermarkBounds = Get-NormalizedBounds $watermark $window
        if ($watermarkBounds.RightInset -lt 24 -or
            $watermarkBounds.BottomInset -lt 24) {
            throw 'Watermark left its 24 DIP footer safe area.'
        }

        # Verify the explicit tab order once; scaling does not change focus order.
        if ([Math]::Abs($scale - 1.0) -lt 0.001) {
            $centerPoint = New-Object Drawing.Point(
                [int](($windowBounds.Left + $windowBounds.Right) / 2),
                [int](($windowBounds.Top + $windowBounds.Bottom) / 2))
            $workArea = [Windows.Forms.Screen]::FromPoint(
                $centerPoint).WorkingArea
            if ($windowBounds.Left -lt $workArea.Left -or
                $windowBounds.Top -lt $workArea.Top -or
                $windowBounds.Right -gt $workArea.Right -or
                $windowBounds.Bottom -gt $workArea.Bottom) {
                throw 'The production-size window is not contained in its target monitor work area.'
            }
            $gear.SetFocus()
            Wait-ForFocusedId 'MajesticBoost.OpenCenter' 1000
            [Windows.Forms.SendKeys]::SendWait('{TAB}')
            Wait-ForFocusedId 'MajesticBoost.Activate' 1000
            [Windows.Forms.SendKeys]::SendWait('{TAB}')
            Wait-ForFocusedId 'MajesticBoost.Keep.DISCORD' 1000
            [Windows.Forms.SendKeys]::SendWait('{TAB}')
            Wait-ForFocusedId 'MajesticBoost.Keep.EPICGAMES' 1000
            [Windows.Forms.SendKeys]::SendWait('{TAB}')
            Wait-ForFocusedId 'MajesticBoost.Keep.STEAM' 1000
        }

        Invoke-Element $gear
        $readiness = Find-ById $window 'MajesticBoost.Center.Tab.Readiness'
        $report = Find-ById $window 'MajesticBoost.Center.Tab.Report'
        $history = Find-ById $window 'MajesticBoost.Center.Tab.History'
        $settings = Find-ById $window 'MajesticBoost.Center.Tab.Settings'
        foreach ($tab in @($readiness, $report, $history, $settings)) {
            Assert-KeyboardFocusable $tab 'Boost Center tab'
        }

        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        Invoke-Element $report
        if ([System.Windows.SystemParameters]::ClientAreaAnimation) {
            Wait-ForFocusedId 'MajesticBoost.Center.ReportBenchmark' 1500
            $stopwatch.Stop()
            Assert-Between `
                $stopwatch.Elapsed.TotalMilliseconds `
                180 `
                900 `
                'Directional report transition'
        }

        Invoke-Element $settings
        $setting = Find-ById $window 'MajesticBoost.Center.Setting.0'
        $settingBounds = Get-NormalizedBounds $setting $window
        if ($settingBounds.RightInset -lt 36) {
            throw "Boost Center switch entered the scroll/toggle safe gutter."
        }
        Assert-KeyboardFocusable $setting 'Boost Center setting switch'
        Start-Sleep -Milliseconds ($transitionMilliseconds + 40)
        $oldReportCondition = New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::AutomationIdProperty,
            'MajesticBoost.Center.ReportBenchmark')
        $oldReportButton = $window.FindFirst(
            [Windows.Automation.TreeScope]::Descendants,
            $oldReportCondition)
        if ($oldReportButton -and -not $oldReportButton.Current.IsOffscreen) {
            throw 'Report and settings content overlap after the directional transition.'
        }

        $restore = Find-ById $window 'MajesticBoost.Center.Restore'
        $restoreBounds = Get-NormalizedBounds $restore $window
        $watermarkBounds = Get-NormalizedBounds $watermark $window
        $horizontalOverlap =
            $restoreBounds.Left -lt ($watermarkBounds.Left + $watermarkBounds.Width) -and
            ($restoreBounds.Left + $restoreBounds.Width) -gt $watermarkBounds.Left
        $verticalOverlap =
            $restoreBounds.Top -lt ($watermarkBounds.Top + $watermarkBounds.Height) -and
            ($restoreBounds.Top + $restoreBounds.Height) -gt $watermarkBounds.Top
        if ($horizontalOverlap -and $verticalOverlap) {
            throw 'Boost Center footer action overlaps the permanent watermark.'
        }

        $results += '{0}%={1:N0}x{2:N0}' -f `
            [int]($scale * 100),
            $windowBounds.Width,
            $windowBounds.Height
    }
    finally {
        if ($process -and -not $process.HasExited) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(3000)) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

$compactProcess = $null
try {
    $compactProcess = Start-Process `
        -FilePath $ApplicationPath `
        -ArgumentList '--skip-setup', '--demo', '--demo-compact' `
        -PassThru
    $compactWindow = Wait-ForWindow `
        -Process $compactProcess `
        -TimeoutMilliseconds 15000
    $compactBounds = $compactWindow.Current.BoundingRectangle
    if ($compactBounds.Height -gt 500) {
        throw "Compact layout height was $($compactBounds.Height); expected at most 500 DIP."
    }
    $compactSteam = Find-ById `
        $compactWindow `
        'MajesticBoost.Keep.STEAM' `
        15000
    $compactSteamBounds = Get-NormalizedBounds `
        $compactSteam `
        $compactWindow
    if ($compactSteamBounds.BottomInset -lt 12) {
        throw 'Compact layout clipped the final preference switch.'
    }
}
finally {
    if ($compactProcess -and -not $compactProcess.HasExited) {
        [void]$compactProcess.CloseMainWindow()
        if (-not $compactProcess.WaitForExit(3000)) {
            Stop-Process `
                -Id $compactProcess.Id `
                -Force `
                -ErrorAction SilentlyContinue
        }
    }
}

$ultraProcess = $null
try {
    $ultraProcess = Start-Process `
        -FilePath $ApplicationPath `
        -ArgumentList '--skip-setup', '--demo', '--demo-ultra-compact' `
        -PassThru
    $ultraWindow = Wait-ForWindow `
        -Process $ultraProcess `
        -TimeoutMilliseconds 15000
    $ultraBounds = $ultraWindow.Current.BoundingRectangle
    if ($ultraBounds.Height -gt 368) {
        throw "Ultra-compact layout height was $($ultraBounds.Height); expected at most 368 DIP."
    }
    foreach ($automationId in @(
        'MajesticBoost.OpenCenter',
        'MajesticBoost.Activate',
        'MajesticBoost.Keep.DISCORD',
        'MajesticBoost.Keep.EPICGAMES',
        'MajesticBoost.Keep.STEAM',
        'MajesticBoost.Close')) {
        $element = Find-ById $ultraWindow $automationId 15000
        $bounds = $element.Current.BoundingRectangle
        if ($bounds.Left -lt $ultraBounds.Left -or
            $bounds.Top -lt $ultraBounds.Top -or
            $bounds.Right -gt $ultraBounds.Right -or
            $bounds.Bottom -gt $ultraBounds.Bottom) {
            throw "Ultra-compact layout clipped $automationId."
        }
    }
}
finally {
    if ($ultraProcess -and -not $ultraProcess.HasExited) {
        [void]$ultraProcess.CloseMainWindow()
        if (-not $ultraProcess.WaitForExit(3000)) {
            Stop-Process `
                -Id $ultraProcess.Id `
                -Force `
                -ErrorAction SilentlyContinue
        }
    }
}

$overlaySource = Get-Content -Raw -Encoding UTF8 (
    Join-Path $projectRoot 'MajesticBoost\BoostCenterOverlay.cs')
if ($overlaySource -notmatch 'SystemParameters\.ClientAreaAnimation') {
    throw 'Boost Center transition no longer respects the Windows animation preference.'
}
$programSource = Get-Content -Raw -Encoding UTF8 (
    Join-Path $projectRoot 'MajesticBoost\Program.cs')
if ($programSource -notmatch 'BuildKeyboardFocusVisualStyle' -or
    $programSource -notmatch 'SystemParameters\.FocusVisualStyleKey' -or
    $programSource -notmatch 'MonitorFromWindow\(' -or
    $programSource -notmatch 'GetMonitorInfo\(' -or
    $programSource -notmatch 'GetDpiForWindow\(' -or
    $programSource -notmatch 'WmDpiChanged' -or
    $programSource -notmatch 'LocationChanged \+=' -or
    $programSource -notmatch 'TransformFromDevice' -or
    $programSource -notmatch 'CalculateMonitorPlacement\(' -or
    $programSource -notmatch 'compactMainLayout' -or
    $programSource -notmatch 'scaleMainLayoutToWorkArea') {
    throw 'Per-monitor DPI/work-area layout or the Majestic keyboard focus visual is missing.'
}
if ($programSource -match 'SystemParameters\.WorkArea') {
    throw 'Main-window placement regressed to the primary-monitor SystemParameters.WorkArea.'
}
$manifest = Get-Content -Raw -Encoding UTF8 (
    Join-Path $projectRoot 'MajesticBoost\app.manifest')
if ($manifest -notmatch '<dpiAwareness[^>]*>PerMonitorV2</dpiAwareness>') {
    throw 'The app manifest no longer declares PerMonitorV2 awareness.'
}
foreach ($sourceText in @(
    $programSource,
    $overlaySource,
    (Get-Content -Raw -Encoding UTF8 (
        Join-Path $projectRoot 'MajesticBoost\OptimizationFlow.cs')),
    (Get-Content -Raw -Encoding UTF8 (
        Join-Path $projectRoot 'MajesticBoost\UpdateFlow.cs')))) {
    if ($sourceText -match 'FocusVisualStyle\s*=\s*null') {
        throw 'A keyboard-focusable application control still suppresses its focus visual.'
    }
}

"Responsive layout test passed: $($results -join ', '); per-monitor mixed-DPI placement, compact/ultra-compact demo layout, keyboard focus, gutters, overlay and $transitionMilliseconds ms motion verified."
