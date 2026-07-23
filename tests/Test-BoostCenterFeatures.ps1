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
    'AssemblyVersion("1.6.1.0")',
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
    'Raise(RestoreRequested)'
)) {
    if (-not $center.Contains($required)) {
        throw "The Boost Center UI contract is missing: $required"
    }
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
