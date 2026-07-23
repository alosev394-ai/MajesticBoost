[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$program = [IO.File]::ReadAllText((Join-Path $projectRoot 'MajesticBoost\Program.cs'))
$center = [IO.File]::ReadAllText((Join-Path $projectRoot 'MajesticBoost\BoostCenterOverlay.cs'))
$features = [IO.File]::ReadAllText((Join-Path $projectRoot 'MajesticBoost\BoostFeatures.cs'))
$capture = [IO.File]::ReadAllText((Join-Path $projectRoot 'MajesticBoost\PerformanceCapture.cs'))
$optimization = [IO.File]::ReadAllText((Join-Path $projectRoot 'MajesticBoost\OptimizationFlow.cs'))
$installer = [IO.File]::ReadAllText((Join-Path $projectRoot 'MajesticBoostInstaller\Program.cs'))
$build = [IO.File]::ReadAllText((Join-Path $projectRoot 'build.ps1'))

foreach ($required in @(
    'AssemblyVersion("1.6.4.0")',
    'AssemblyCompany("Silus Suspect")',
    'GetApplicationVersion() + "  BETA"',
    'MakeText(',
    '"by Silus Suspect"',
    'Panel.SetZIndex(watermark, 400)',
    'ProcessPriorityClass.AboveNormal',
    'OriginalPriority = originalPriority',
    'process.StartTime.ToUniversalTime() != item.StartTimeUtc',
    'current != ProcessPriorityClass.AboveNormal',
    'BoostActionOutcome.ExternalOverridePreserved',
    'Interval = TimeSpan.FromSeconds(5)',
    'AutoBoost=" + centerSettings.AutoBoost',
    'CheckBeforeBoost=" + centerSettings.CheckBeforeBoost',
    'Interlocked.Increment(ref preflightGeneration)',
    'generation != Interlocked.CompareExchange(ref preflightGeneration, 0, 0)',
    'lastSession.Complete(',
    '"Interrupted"',
    'BoostSessionReportStore.Save',
    'PerformanceCaptureService.CaptureRunningGameAsync'
)) {
    if (-not $program.Contains($required)) {
        throw "The Boost session contract is missing: $required"
    }
}

$windowControlsStart = $program.IndexOf(
    'private Grid BuildWindowControls',
    [StringComparison]::Ordinal)
$titleStart = $program.IndexOf(
    'private StackPanel BuildTitle',
    $windowControlsStart,
    [StringComparison]::Ordinal)
if ($windowControlsStart -lt 0 -or $titleStart -le $windowControlsStart) {
    throw 'The main-window chrome composition could not be located.'
}
$windowControls = $program.Substring(
    $windowControlsStart,
    $titleStart - $windowControlsStart)
foreach ($required in @(
    'header.Margin = new Thickness(0)',
    'center.HorizontalAlignment = HorizontalAlignment.Left',
    'center.VerticalAlignment = VerticalAlignment.Top',
    'header.Children.Add(center);',
    'controls.HorizontalAlignment = HorizontalAlignment.Right',
    'controls.VerticalAlignment = VerticalAlignment.Top'
)) {
    if (-not $windowControls.Contains($required)) {
        throw "The split left/right window-chrome contract is missing: $required"
    }
}
if ($windowControls.Contains('controls.Children.Add(center)')) {
    throw 'The settings button is still grouped with the right-side caption controls.'
}

$centerButtonStart = $program.IndexOf(
    'private static Button MakeCenterButton',
    [StringComparison]::Ordinal)
$windowButtonStart = $program.IndexOf(
    'private static Button MakeWindowButton',
    $centerButtonStart,
    [StringComparison]::Ordinal)
$transparentButtonTemplateStart = $program.IndexOf(
    'private static ControlTemplate MakeTransparentButtonTemplate',
    $windowButtonStart,
    [StringComparison]::Ordinal)
$chromeButtonTemplateStart = $program.IndexOf(
    'private static ControlTemplate MakeChromeButtonTemplate',
    $transparentButtonTemplateStart,
    [StringComparison]::Ordinal)
$makeTextStart = $program.IndexOf(
    'private static TextBlock MakeText',
    $chromeButtonTemplateStart,
    [StringComparison]::Ordinal)
if ($centerButtonStart -lt 0 -or
    $windowButtonStart -le $centerButtonStart -or
    $transparentButtonTemplateStart -le $windowButtonStart -or
    $chromeButtonTemplateStart -le $transparentButtonTemplateStart -or
    $makeTextStart -le $chromeButtonTemplateStart) {
    throw 'The window chrome geometry sections could not be located.'
}
$centerButton = $program.Substring(
    $centerButtonStart,
    $windowButtonStart - $centerButtonStart)
$windowButton = $program.Substring(
    $windowButtonStart,
    $transparentButtonTemplateStart - $windowButtonStart)
$chromeButtonTemplate = $program.Substring(
    $chromeButtonTemplateStart,
    $makeTextStart - $chromeButtonTemplateStart)
foreach ($required in @(
    'FontSize = 17',
    'RenderTransform = new TranslateTransform(0, -1)'
)) {
    if (-not $centerButton.Contains($required)) {
        throw "The centered settings glyph contract is missing: $required"
    }
}
foreach ($required in @(
    'glyphCanvas.Width = 30',
    'glyphCanvas.Height = 30',
    'Geometry.Parse("M 10,10 L 20,20 M 20,10 L 10,20")',
    'minimizeGlyph.Width = 16',
    'Canvas.SetLeft(minimizeGlyph, 7)',
    'Canvas.SetTop(minimizeGlyph, 20)'
)) {
    if (-not $windowButton.Contains($required)) {
        throw "The centered caption glyph contract is missing: $required"
    }
}
if (-not $chromeButtonTemplate.Contains(
        'BorderThicknessProperty, new Thickness(0)')) {
    throw 'The window chrome template still insets its glyph content.'
}

foreach ($required in @(
    'elevated && !IsTrustedInstalledToolPath(tool.Path)',
    'CreateElevatedCapturePath()',
    'Environment.SpecialFolder.CommonApplicationData',
    'ResolveProtectedCaptureDirectory()',
    'ValidateElevatedCaptureFile(elevatedOutputPath);',
    'File.Copy(elevatedOutputPath, csvPath, false)',
    'FileAttributes.ReparsePoint',
    'ExpectedPresentMonSha256'
)) {
    if (-not $capture.Contains($required)) {
        throw "The elevated measurement safety contract is missing: $required"
    }
}

foreach ($required in @(
    'PrepareCaptureDirectoryTransaction',
    'ApplyCaptureDirectoryTransaction',
    'RollbackCaptureDirectoryTransaction',
    'SetAccessRuleProtection(true, false)',
    'WellKnownSidType.LocalSystemSid',
    'WellKnownSidType.BuiltinAdministratorsSid',
    'WellKnownSidType.AuthenticatedUserSid',
    'FileSystemRights.ReadAndExecute',
    'PropagationFlags.InheritOnly'
)) {
    if (-not $installer.Contains($required)) {
        throw "The protected ProgramData capture ACL contract is missing: $required"
    }
}

$maintenanceStart = $program.IndexOf('private void RunActiveBoostMaintenance', [StringComparison]::Ordinal)
$restoreStart = $program.IndexOf('private void RestoreOwnedGamePriorities', $maintenanceStart, [StringComparison]::Ordinal)
if ($maintenanceStart -lt 0 -or $restoreStart -le $maintenanceStart) {
    throw 'The Active Boost maintenance section could not be located.'
}
$maintenance = $program.Substring($maintenanceStart, $restoreStart - $maintenanceStart)
foreach ($forbidden in @(
    'ProcessPriorityClass.High',
    'ProcessPriorityClass.RealTime',
    '.Kill()',
    'Discord',
    'steamwebhelper',
    'EpicGamesLauncher',
    'NVIDIA Overlay',
    'wallpaper64'
)) {
    if ($maintenance.Contains($forbidden)) {
        throw "Active maintenance contains forbidden repeated behavior: $forbidden"
    }
}

foreach ($required in @(
    'CenterPage.Readiness',
    'CenterPage.Report',
    'CenterPage.Settings',
    'OpenReadiness',
    'OpenReport',
    'OpenSettings',
    'AutomationProperties.SetName',
    'KeyboardNavigationMode.Cycle',
    'SystemParameters.ClientAreaAnimation',
    'Color.FromRgb(232, 28, 90)',
    'MakeMajesticVerticalScrollBarStyle',
    'CanContentScroll = false',
    'PageScrollerPreviewMouseWheel',
    'CalculateSmoothScrollTarget',
    'Thumb.DragStartedEvent',
    'BeginPageTransition(previousPage, page);',
    'FinishPageTransitionImmediately();',
    'generation != pageTransitionGeneration',
    '-direction * 18',
    'direction * 18',
    'TimeSpan.FromMilliseconds(milliseconds)',
    'AnimateTabColor(foreground, targetColor, animate);',
    'AnimateTabIndicator(',
    'FillBehavior = FillBehavior.Stop',
    'Raise(RestoreRequested)'
)) {
    if (-not $center.Contains($required)) {
        throw "The Boost Center UI contract is missing: $required"
    }
}
if ($center -notmatch '(?s)AnimatePageVisual\(\s*pageScroller,.*?90,\s*EasingMode\.EaseIn' -or
    $center -notmatch '(?s)AnimatePageVisual\(\s*pageScroller,.*?130,\s*EasingMode\.EaseOut') {
    throw 'The Boost Center page transition timing contract is missing.'
}

$mainToggleStart = $program.IndexOf(
    'private CheckBox BuildPreferenceToggle',
    [StringComparison]::Ordinal)
$mainToggleEnd = $program.IndexOf(
    'private void PreferenceToggleChanged',
    $mainToggleStart,
    [StringComparison]::Ordinal)
$mainToggleTemplateStart = $program.IndexOf(
    'private static ControlTemplate MakeTransparentCheckBoxTemplate',
    [StringComparison]::Ordinal)
$mainToggleTemplateEnd = $program.IndexOf(
    'private static ControlTemplate MakeChromeButtonTemplate',
    $mainToggleTemplateStart,
    [StringComparison]::Ordinal)
if ($mainToggleStart -lt 0 -or $mainToggleEnd -le $mainToggleStart -or
    $mainToggleTemplateStart -lt 0 -or
    $mainToggleTemplateEnd -le $mainToggleTemplateStart) {
    throw 'The main toggle geometry sections could not be located.'
}
$mainToggle = $program.Substring($mainToggleStart, $mainToggleEnd - $mainToggleStart)
$mainToggleTemplate = $program.Substring(
    $mainToggleTemplateStart,
    $mainToggleTemplateEnd - $mainToggleTemplateStart)
foreach ($required in @(
    'content.HorizontalAlignment = HorizontalAlignment.Stretch',
    'Width = new GridLength(44)',
    'track.Margin = new Thickness(0, 0, 4, 0)',
    'knob.Margin = new Thickness(3, 0, 0, 0)',
    'content.UseLayoutRounding = true',
    'track.ClipToBounds = false'
)) {
    if (-not $mainToggle.Contains($required)) {
        throw "The main toggle anti-clipping contract is missing: $required"
    }
}
if ($mainToggle.Contains('content.Width = 300') -or
    -not $mainToggleTemplate.Contains(
        'BorderThicknessProperty, new Thickness(0)') -or
    -not $program.Contains('double targetX = isChecked ? 14 : 0')) {
    throw 'The main toggle template can still clip its rounded right edge.'
}
foreach ($required in @(
    'Margin = new Thickness(0, 8, 4, 8)',
    'Width = new GridLength(44)',
    'Margin = new Thickness(0, 0, 4, 0)',
    'Margin = new Thickness(3, 0, 0, 0)',
    'new TranslateTransform(isChecked ? 14 : 0, 0)',
    'double targetX = active ? 14 : 0',
    'UseLayoutRounding = true',
    'ClipToBounds = false'
)) {
    if (-not $center.Contains($required)) {
        throw "The Boost Center toggle anti-clipping contract is missing: $required"
    }
}
if (-not $installer.Contains('float trackLeft = Width - 38F;')) {
    throw 'The installer toggle does not preserve its rounded right-edge inset.'
}

foreach ($forbidden in @(
    'GetPreference(values, "KeepDiscord")',
    'GetPreference(values, "KeepEpic")',
    'GetPreference(values, "KeepSteam")',
    '"KeepDiscord="',
    '"KeepEpic="',
    '"KeepSteam="',
    'IsKeyboardFocusedProperty'
)) {
    if ($program.Contains($forbidden) -or $center.Contains($forbidden)) {
        throw "The interaction-style contract still contains: $forbidden"
    }
}
if ($installer.Contains('DrawFocusRectangle')) {
    throw 'The installer still draws a click/focus rectangle.'
}

foreach ($required in @(
    'internal string GetOptimizationStatus()',
    'internal bool ShowManualRestore()',
    'BeginRestoreAndClose()'
)) {
    if (-not $optimization.Contains($required)) {
        throw "The manual restore contract is missing: $required"
    }
}

foreach ($required in @(
    'WriteAllTextAtomic',
    'MaxReports = 20',
    'BoostPreflightService',
    'AvailableMemoryStartBytes',
    'ExternalOverridePreserved'
)) {
    if (-not $features.Contains($required)) {
        throw "The report/preflight contract is missing: $required"
    }
}

foreach ($required in @(
    'BoostCenterOverlay.cs',
    'PerformanceCapture.cs',
    'Pinned PresentMon 2.5.1',
    'MajesticBoost.PresentMon.exe'
)) {
    if (-not $build.Contains($required)) {
        throw "The release build contract is missing: $required"
    }
}

Write-Host 'Boost Center and safe session regression test passed.' -ForegroundColor Green
