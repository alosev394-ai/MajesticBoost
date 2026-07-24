[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $projectRoot 'MajesticBoostInstaller\Program.cs'
$source = [IO.File]::ReadAllText($sourcePath)
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'MajesticBoost-InstallerLifecycle-' + [Guid]::NewGuid().ToString('N'))
$harnessPath = Join-Path $temporaryRoot (
    'InstallerLifecycleHarness-' + [Guid]::NewGuid().ToString('N') + '.dll')
$stateRoot = Join-Path $temporaryRoot 'CodexGamingOptimization'
$utf8 = New-Object Text.UTF8Encoding($false)

function Get-DeepestException {
    param([Parameter(Mandatory = $true)][Exception]$Exception)

    $current = $Exception
    while ($current.InnerException) {
        $current = $current.InnerException
    }
    return $current
}

function Assert-InvocationFails {
    param(
        [Parameter(Mandatory = $true)][Reflection.MethodInfo]$Method,
        [Parameter(Mandatory = $true)][object[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Scenario,
        [Type]$ExpectedType = [InvalidOperationException]
    )

    try {
        $result = $Method.Invoke($null, $Arguments)
        if ($result -is [IDisposable]) {
            $result.Dispose()
        }
    }
    catch {
        $current = $_.Exception
        while ($current) {
            if ($ExpectedType.IsAssignableFrom($current.GetType())) {
                return
            }
            $current = $current.InnerException
        }
        $actual = Get-DeepestException -Exception $_.Exception
        throw "$Scenario failed with $($actual.GetType().FullName), expected $($ExpectedType.FullName): $($actual.Message)"
    }
    throw "$Scenario unexpectedly succeeded."
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    [IO.File]::WriteAllText($Path, $Content, $utf8)
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw "C# compiler was not found: $compiler"
    }

    $compilerOutput = & $compiler `
        /nologo `
        /target:library `
        /utf8output `
        "/out:$harnessPath" `
        /reference:System.dll `
        /reference:System.Core.dll `
        /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll `
        /reference:System.Security.dll `
        $sourcePath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Installer source did not compile:`r`n$($compilerOutput -join [Environment]::NewLine)"
    }

    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($harnessPath))
    $engineType = $assembly.GetType('MajesticBoostSetup.InstallerEngine', $true, $false)
    $flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
    $acquireGuard = $engineType.GetMethod(
        'AcquireSystemTransactionGuardAtRoot',
        $flags)
    $validateUninstall = $engineType.GetMethod(
        'EnsureUninstallStateAllowsRemovalAtRoot',
        $flags)
    if (-not $acquireGuard -or -not $validateUninstall) {
        throw 'Installer lifecycle safety helpers were not found in the compiled source.'
    }

    # This uses only a unique temporary root. It never opens the real ProgramData lock.
    $guardArguments = New-Object 'object[]' 2
    $guardArguments[0] = [string]$stateRoot
    $guardArguments[1] = [string]'test operation'
    $guard = [IDisposable]$acquireGuard.Invoke(
        $null,
        $guardArguments)
    try {
        $secondGuardArguments = New-Object 'object[]' 2
        $secondGuardArguments[0] = [string]$stateRoot
        $secondGuardArguments[1] = [string]'second test operation'
        Assert-InvocationFails `
            -Method $acquireGuard `
            -Arguments $secondGuardArguments `
            -Scenario 'A second operation while transaction.lock is held'
    }
    finally {
        $guard.Dispose()
    }

    $releasedGuardArguments = New-Object 'object[]' 2
    $releasedGuardArguments[0] = [string]$stateRoot
    $releasedGuardArguments[1] = [string]'operation after release'
    $guardAfterRelease = [IDisposable]$acquireGuard.Invoke(
        $null,
        $releasedGuardArguments)
    $guardAfterRelease.Dispose()

    $backupRoot = Join-Path $stateRoot 'Backups'
    $backupDirectory = Join-Path $backupRoot 'test-transaction'
    [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
    $statePath = Join-Path $backupDirectory 'state.json'
    $pointerPath = Join-Path $stateRoot 'latest-state.txt'

    Write-Utf8File -Path $statePath -Content '{"Version":2,"Status":"Active"}'
    Write-Utf8File -Path $pointerPath -Content $statePath
    $stateArguments = New-Object 'object[]' 1
    $stateArguments[0] = [string]$stateRoot
    Assert-InvocationFails `
        -Method $validateUninstall `
        -Arguments $stateArguments `
        -Scenario 'Uninstall with an Active optimization transaction'

    Write-Utf8File -Path $statePath -Content '{"Version":2,"Status":"Restored"}'
    [void]$validateUninstall.Invoke($null, $stateArguments)

    Write-Utf8File -Path $statePath -Content '{"Version":2,"Status":"UnknownState"}'
    Assert-InvocationFails `
        -Method $validateUninstall `
        -Arguments $stateArguments `
        -Scenario 'Uninstall with an ambiguous transaction status'

    $nestedDirectory = Join-Path $backupDirectory 'nested'
    [IO.Directory]::CreateDirectory($nestedDirectory) | Out-Null
    $nestedState = Join-Path $nestedDirectory 'state.json'
    Write-Utf8File -Path $nestedState -Content '{"Version":2,"Status":"Restored"}'
    Write-Utf8File -Path $pointerPath -Content $nestedState
    Assert-InvocationFails `
        -Method $validateUninstall `
        -Arguments $stateArguments `
        -Scenario 'Uninstall with state.json below a nested backup directory'

    Write-Utf8File -Path $pointerPath -Content ('x' * 4097)
    Assert-InvocationFails `
        -Method $validateUninstall `
        -Arguments $stateArguments `
        -Scenario 'Uninstall with an oversized latest-state pointer'

    $installStart = $source.IndexOf(
        'public static void Install(bool createDesktopShortcut',
        [StringComparison]::Ordinal)
    $installGuard = $source.IndexOf(
        'AcquireSystemTransactionGuard(',
        $installStart,
        [StringComparison]::Ordinal)
    $installCore = $source.IndexOf(
        'InstallWithSystemTransactionGuard(createDesktopShortcut, progress);',
        $installGuard,
        [StringComparison]::Ordinal)
    if ($installStart -lt 0 -or $installGuard -le $installStart -or
        $installCore -le $installGuard) {
        throw 'Install does not acquire and retain the system transaction guard before mutation.'
    }

    $uninstallStart = $source.IndexOf(
        'public static void Uninstall(bool quiet)',
        [StringComparison]::Ordinal)
    $uninstallGuard = $source.IndexOf(
        'AcquireSystemTransactionGuard(',
        $uninstallStart,
        [StringComparison]::Ordinal)
    $uninstallStateCheck = $source.IndexOf(
        'EnsureUninstallStateAllowsRemoval();',
        $uninstallGuard,
        [StringComparison]::Ordinal)
    $uninstallStop = $source.IndexOf(
        'StopInstalledApplication();',
        $uninstallStateCheck,
        [StringComparison]::Ordinal)
    if ($uninstallStart -lt 0 -or $uninstallGuard -le $uninstallStart -or
        $uninstallStateCheck -le $uninstallGuard -or
        $uninstallStop -le $uninstallStateCheck) {
        throw 'Uninstall must acquire the lock and validate recovery state before stopping the app.'
    }

    foreach ($requiredContract in @(
        'FileShare.None',
        'FileAttributes.ReparsePoint',
        'OptimizationStatePointerMaximumBytes',
        'OptimizationStateMaximumBytes',
        'IsDirectChildPath',
        'CreateUnsafeUninstallStateException',
        'if (quiet)',
        'throw;'
    )) {
        if (-not $source.Contains($requiredContract)) {
            throw "Installer lifecycle safety contract is missing: $requiredContract"
        }
    }

    $formStart = $source.IndexOf(
        'internal sealed class InstallerForm',
        [StringComparison]::Ordinal)
    $formSection = $source.Substring($formStart)
    foreach ($asyncContract in @(
        'installOperationRunning',
        'System.Threading.ThreadPool.QueueUserWorkItem',
        'ReportInstallProgressFromWorker',
        'PostToUi',
        'BeginInvoke(action)',
        'protected override void OnFormClosing'
    )) {
        if (-not $formSection.Contains($asyncContract)) {
            throw "Installer UI async contract is missing: $asyncContract"
        }
    }
    if ($formSection.Contains('InstallerEngine.Install(desktopShortcut.Checked);') -or
        $formSection.Contains('Application.DoEvents();')) {
        throw 'InstallerForm still runs installation synchronously or pumps nested UI messages.'
    }

    Write-Host 'Installer lifecycle safety regression test passed.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
        $systemTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
        $expectedPrefix = $systemTemporaryRoot + '\MajesticBoost-InstallerLifecycle-'
        if ($resolvedTemporaryRoot.StartsWith(
                $expectedPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
        }
    }
}
