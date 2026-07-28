[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$installerSource = Join-Path $projectRoot 'MajesticBoostInstaller\Program.cs'
$updateSource = Join-Path $projectRoot 'MajesticBoost\UpdateFlow.cs'
$applicationSource = Join-Path $projectRoot 'MajesticBoost\Program.cs'
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$wpfRoot = Join-Path $frameworkRoot 'WPF'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'MajesticBoost-UpdateRollback-' + [Guid]::NewGuid().ToString('N'))
$installerHarness = Join-Path $temporaryRoot 'InstallerRollbackHarness.dll'
$applicationHarness = Join-Path $temporaryRoot 'ApplicationHandshakeHarness.dll'
$utf8 = New-Object Text.UTF8Encoding($false)

function Get-InnerException {
    param([Parameter(Mandatory = $true)][Exception]$Exception)

    $current = $Exception
    while (($current -is [Reflection.TargetInvocationException] -or
        $current -is [Management.Automation.MethodInvocationException]) -and
        $current.InnerException) {
        $current = $current.InnerException
    }
    return $current
}

function Get-StaticMethod {
    param(
        [Parameter(Mandatory = $true)][Type]$Type,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $flags = [Reflection.BindingFlags]::NonPublic -bor
        [Reflection.BindingFlags]::Static
    $method = $Type.GetMethod($Name, $flags)
    if (-not $method) {
        throw "Required rollback method was not found: $($Type.FullName).$Name"
    }
    return $method
}

function Invoke-Static {
    param(
        [Parameter(Mandatory = $true)][Reflection.MethodInfo]$Method,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [AllowNull()]
        [object[]]$Arguments
    )

    if ($null -eq $Arguments) {
        $Arguments = New-Object 'object[]' 0
    }
    try {
        return $Method.Invoke($null, $Arguments)
    }
    catch {
        throw (Get-InnerException -Exception $_.Exception)
    }
}

function Write-TestFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Value, $utf8)
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw "C# compiler was not found: $compiler"
    }

    $installerCompilerOutput = & $compiler `
        /nologo `
        /target:library `
        /utf8output `
        "/out:$installerHarness" `
        /reference:System.dll `
        /reference:System.Core.dll `
        /reference:System.Drawing.dll `
        /reference:System.Windows.Forms.dll `
        /reference:System.Security.dll `
        $installerSource 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Installer rollback harness did not compile:`r`n$($installerCompilerOutput -join [Environment]::NewLine)"
    }

    $applicationCompilerOutput = & $compiler `
        /nologo `
        /target:library `
        /utf8output `
        "/out:$applicationHarness" `
        /reference:System.dll `
        /reference:System.Core.dll `
        "/reference:$frameworkRoot\System.Xaml.dll" `
        "/reference:$wpfRoot\WindowsBase.dll" `
        "/reference:$wpfRoot\PresentationCore.dll" `
        "/reference:$wpfRoot\PresentationFramework.dll" `
        $updateSource 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Application health-handshake harness did not compile:`r`n$($applicationCompilerOutput -join [Environment]::NewLine)"
    }

    $installerAssembly = [Reflection.Assembly]::Load(
        [IO.File]::ReadAllBytes($installerHarness))
    $applicationAssembly = [Reflection.Assembly]::Load(
        [IO.File]::ReadAllBytes($applicationHarness))
    $engineType = $installerAssembly.GetType(
        'MajesticBoostSetup.InstallerEngine',
        $true,
        $false)
    $handshakeType = $applicationAssembly.GetType(
        'MajesticBoost.UpdateHealthHandshake',
        $true,
        $false)

    $createSnapshot = Get-StaticMethod `
        -Type $engineType `
        -Name 'CreateFileSnapshot'
    $runRecoveryWatchdog = $engineType.GetMethod(
        'TryRunUpdateRecoveryWatchdog',
        [Reflection.BindingFlags]::Public -bor
        [Reflection.BindingFlags]::Static)
    if (-not $runRecoveryWatchdog) {
        throw 'The recovery-watchdog entry point was not found.'
    }
    $restoreSnapshot = Get-StaticMethod `
        -Type $engineType `
        -Name 'RestoreFileSnapshotAtPaths'
    $validateRelativePath = Get-StaticMethod `
        -Type $engineType `
        -Name 'ValidateRelativeSnapshotPath'
    $createToken = Get-StaticMethod `
        -Type $engineType `
        -Name 'CreateCryptographicToken'
    $installerProof = Get-StaticMethod `
        -Type $engineType `
        -Name 'ComputeReadyProof'
    $validateReady = Get-StaticMethod `
        -Type $engineType `
        -Name 'TryValidateReadySignal'
    $applicationProof = Get-StaticMethod `
        -Type $handshakeType `
        -Name 'ComputeReadyProof'
    $parseProbe = Get-StaticMethod `
        -Type $handshakeType `
        -Name 'TryReadProbeArguments'
    $completeProbe = Get-StaticMethod `
        -Type $handshakeType `
        -Name 'CompleteReadyHandshakeIfRequested'

    $transactionId = [Guid]::NewGuid().ToString('N')
    $token = [string](Invoke-Static `
        -Method $createToken `
        -Arguments ([object[]]@()))
    if ($token -cnotmatch '^[0-9a-f]{64}$') {
        throw 'The update health token is not a 256-bit lowercase random value.'
    }
    $ownerSid = 'S-1-5-21-1000-1001-1002-1003'
    $expectedVersion = '1.8.0.0'

    $proofArguments = New-Object 'object[]' 4
    $proofArguments[0] = $transactionId
    $proofArguments[1] = $token
    $proofArguments[2] = $ownerSid
    $proofArguments[3] = $expectedVersion
    $installerProofValue = [string](Invoke-Static `
        -Method $installerProof `
        -Arguments $proofArguments)
    $applicationProofValue = [string](Invoke-Static `
        -Method $applicationProof `
        -Arguments $proofArguments)
    if ($installerProofValue -cne $applicationProofValue) {
        throw 'Installer and application health-proof protocols disagree.'
    }

    $probeArguments = @(
        '--update-health-probe',
        ('--update-transaction=' + $transactionId),
        ('--update-health-token=' + $token),
        ('--update-health-owner=' + $ownerSid)
    )
    $parseArguments = New-Object 'object[]' 5
    $parseArguments[0] = [string[]]$probeArguments
    $parseArguments[1] = $false
    $parseArguments[2] = $null
    $parseArguments[3] = $null
    $parseArguments[4] = $null
    if (-not [bool]$parseProbe.Invoke($null, $parseArguments) -or
        -not [bool]$parseArguments[1] -or
        [string]$parseArguments[2] -cne $transactionId -or
        [string]$parseArguments[3] -cne $token -or
        [string]$parseArguments[4] -cne $ownerSid) {
        throw 'The application rejected the installer health-probe arguments.'
    }

    $tamperedProbe = [string[]]$probeArguments.Clone()
    $tamperedProbe[2] = '--update-health-token=' + ('A' + $token.Substring(1))
    $parseArguments[0] = $tamperedProbe
    $parseArguments[1] = $false
    $parseArguments[2] = $null
    $parseArguments[3] = $null
    $parseArguments[4] = $null
    if ([bool]$parseProbe.Invoke($null, $parseArguments) -or
        -not [bool]$parseArguments[1]) {
        throw 'A tampered probe token was accepted or lost its probe-only fail-closed state.'
    }

    $normalLaunchArguments = New-Object 'object[]' 1
    $normalLaunchArguments[0] = [string[]]@()
    if ([bool](Invoke-Static `
            -Method $completeProbe `
            -Arguments $normalLaunchArguments)) {
        throw 'A normal application launch was mistaken for an update health probe.'
    }

    $malformedRecoveryArguments = New-Object 'object[]' 1
    $malformedRecoveryArguments[0] = [string[]]@(
        '/update-recovery',
        '..\tampered',
        '0')
    if (-not [bool]$runRecoveryWatchdog.Invoke(
            $null,
            $malformedRecoveryArguments)) {
        throw 'A malformed recovery invocation fell through into the normal installer.'
    }

    $scenarioRoot = Join-Path $temporaryRoot 'scenario'
    $installParent = Join-Path $scenarioRoot 'ProgramFiles'
    $installDirectory = Join-Path $installParent 'Majestic Boost'
    $transactionDirectory = Join-Path $scenarioRoot $transactionId
    $snapshotDirectory = Join-Path $transactionDirectory 'snapshot\files'
    $manifestPath = Join-Path $transactionDirectory 'files.manifest'
    [IO.Directory]::CreateDirectory($snapshotDirectory) | Out-Null
    Write-TestFile `
        -Path (Join-Path $installDirectory 'MajesticBoost.exe') `
        -Value 'old-application'
    Write-TestFile `
        -Path (Join-Path $installDirectory 'Game-Boost.ps1') `
        -Value 'old-script'
    Write-TestFile `
        -Path (Join-Path $installDirectory 'Tools\PresentMon\LICENSE.txt') `
        -Value 'old-license'

    $snapshotArguments = New-Object 'object[]' 3
    $snapshotArguments[0] = [string]$installDirectory
    $snapshotArguments[1] = [string]$snapshotDirectory
    $snapshotArguments[2] = [string]$manifestPath
    [void](Invoke-Static `
        -Method $createSnapshot `
        -Arguments $snapshotArguments)

    Write-TestFile `
        -Path (Join-Path $installDirectory 'MajesticBoost.exe') `
        -Value 'new-application'
    Write-TestFile `
        -Path (Join-Path $installDirectory 'new-resource.bin') `
        -Value 'new-only'

    $readyContent =
        "Format=1`n" +
        "Transaction=$transactionId`n" +
        "ReadySid=$ownerSid`n" +
        "ReadyVersion=$expectedVersion`n" +
        "Proof=$installerProofValue`n"
    Write-TestFile `
        -Path (Join-Path $transactionDirectory 'ready.signal') `
        -Value $readyContent
    $readyArguments = New-Object 'object[]' 6
    $readyArguments[0] = [string]$transactionDirectory
    $readyArguments[1] = [string]$transactionId
    $readyArguments[2] = [string]$token
    $readyArguments[3] = [string]$ownerSid
    $readyArguments[4] = [string]$expectedVersion
    $readyArguments[5] = $false
    if (-not [bool]$validateReady.Invoke($null, $readyArguments) -or
        [bool]$readyArguments[5]) {
        throw 'A valid ready signal was rejected.'
    }
    if ([IO.File]::ReadAllText(
            (Join-Path $installDirectory 'MajesticBoost.exe')) -cne
        'new-application') {
        throw 'The success scenario replaced the healthy new version.'
    }

    $tamperedToken = ('0' + $token.Substring(1))
    if ($tamperedToken -ceq $token) {
        $tamperedToken = ('1' + $token.Substring(1))
    }
    $readyArguments[2] = $tamperedToken
    $readyArguments[5] = $false
    if ([bool]$validateReady.Invoke($null, $readyArguments) -or
        -not [bool]$readyArguments[5]) {
        throw 'A ready signal with a tampered token was accepted.'
    }

    # Timeout/no-ready path: restoration replaces the entire new directory, so
    # new-only files cannot influence the old executable after rollback.
    [IO.File]::Delete((Join-Path $transactionDirectory 'ready.signal'))
    $restoreArguments = New-Object 'object[]' 4
    $restoreArguments[0] = [string]$snapshotDirectory
    $restoreArguments[1] = [string]$manifestPath
    $restoreArguments[2] = [string]$installDirectory
    $restoreArguments[3] = [string]$transactionId
    [void](Invoke-Static `
        -Method $restoreSnapshot `
        -Arguments $restoreArguments)
    if ([IO.File]::ReadAllText(
            (Join-Path $installDirectory 'MajesticBoost.exe')) -cne
        'old-application' -or
        (Test-Path -LiteralPath (
            Join-Path $installDirectory 'new-resource.bin'))) {
        throw 'The timeout scenario did not restore the exact previous installation.'
    }

    # A second recovery pass models an installer/watchdog interruption after the
    # durable RollingBack marker. Restoration is deliberately idempotent.
    [void](Invoke-Static `
        -Method $restoreSnapshot `
        -Arguments $restoreArguments)
    if ([IO.File]::ReadAllText(
            (Join-Path $installDirectory 'MajesticBoost.exe')) -cne
        'old-application') {
        throw 'Interrupted rollback recovery was not idempotent.'
    }

    # Corrupt snapshots fail before the current installation is renamed.
    Write-TestFile `
        -Path (Join-Path $installDirectory 'MajesticBoost.exe') `
        -Value 'new-after-corruption'
    $snapshotApp = Join-Path $snapshotDirectory 'MajesticBoost.exe'
    [IO.File]::AppendAllText($snapshotApp, 'tampered', $utf8)
    try {
        [void](Invoke-Static `
            -Method $restoreSnapshot `
            -Arguments $restoreArguments)
        throw 'A corrupt rollback snapshot was accepted.'
    }
    catch {
        $failure = Get-InnerException -Exception $_.Exception
        if ($failure -isnot [IO.InvalidDataException]) {
            throw $failure
        }
    }
    if ([IO.File]::ReadAllText(
            (Join-Path $installDirectory 'MajesticBoost.exe')) -cne
        'new-after-corruption') {
        throw 'A corrupt snapshot modified the current installation.'
    }

    $traversalArguments = New-Object 'object[]' 1
    $traversalArguments[0] = '..\outside.exe'
    try {
        [void](Invoke-Static `
            -Method $validateRelativePath `
            -Arguments $traversalArguments)
        throw 'A path-traversal entry was accepted by the snapshot contract.'
    }
    catch {
        $failure = Get-InnerException -Exception $_.Exception
        if ($failure -isnot [IO.InvalidDataException]) {
            throw $failure
        }
    }

    $installerText = [IO.File]::ReadAllText($installerSource)
    $updateText = [IO.File]::ReadAllText($updateSource)
    $applicationText = [IO.File]::ReadAllText($applicationSource)
    foreach ($required in @(
        'UpdateHealthTimeoutMilliseconds = 30000',
        'RandomNumberGenerator.Create()',
        'FileOptions.WriteThrough',
        'UpdateRollbackStatus.RollingBack',
        'ValidateDirectoryTreeWithoutReparse',
        'RecoverInterruptedUpdateTransactions(false)',
        'StartUpdateRecoveryWatchdog(transaction)',
        'RestorePostInstallRegistration(registration)',
        'string.Equals(argument, "/updateui"',
        'if (File.Exists(InstalledExe))'
    )) {
        if (-not $installerText.Contains($required)) {
            throw "Installer rollback contract is missing: $required"
        }
    }
    foreach ($required in @(
        'CompleteReadyHandshakeIfRequested',
        '--update-health-probe',
        'ValidateProtectedTransactionDirectory',
        'FixedTimeEquals',
        'WriteReadySignalAtomically'
    )) {
        if (-not $updateText.Contains($required)) {
            throw "Application ready-handshake contract is missing: $required"
        }
    }
    $loadedStart = $applicationText.IndexOf(
        'private async void BoostWindowLoaded',
        [StringComparison]::Ordinal)
    $readinessCall = $applicationText.IndexOf(
        'await VerifyLocalStartupForUpdateAsync();',
        $loadedStart,
        [StringComparison]::Ordinal)
    $handshakeCall = $applicationText.IndexOf(
        'UpdateHealthHandshake.CompleteReadyHandshakeIfRequested(',
        $readinessCall,
        [StringComparison]::Ordinal)
    $networkCheck = $applicationText.IndexOf(
        'await updateOverlay.CheckForUpdatesAsync();',
        $handshakeCall,
        [StringComparison]::Ordinal)
    if ($loadedStart -lt 0 -or
        $readinessCall -le $loadedStart -or
        $handshakeCall -le $readinessCall -or
        $networkCheck -le $handshakeCall) {
        throw 'The update probe can signal ready before local startup or after network work.'
    }
    $readinessStart = $applicationText.IndexOf(
        'private async Task VerifyLocalStartupForUpdateAsync()',
        [StringComparison]::Ordinal)
    $readinessEnd = $applicationText.IndexOf(
        'private void BoostWindowClosing',
        $readinessStart,
        [StringComparison]::Ordinal)
    if ($readinessStart -lt 0 -or $readinessEnd -le $readinessStart) {
        throw 'The local update-readiness verification method could not be located.'
    }
    $readinessText = $applicationText.Substring(
        $readinessStart,
        $readinessEnd - $readinessStart)
    foreach ($required in @(
        'BoostSessionReportStore.LoadLast()',
        'DiagnosticSessionHistory.LoadRecent(',
        'BoostPreflightService.Run(',
        'DiagnosticSnapshotProvider.Capture()',
        'StartGameWatcher(false);',
        'DispatcherPriority.ApplicationIdle',
        'gameWatcherTimer.IsEnabled'
    )) {
        if (-not $readinessText.Contains($required)) {
            throw "The local update-readiness contract is missing: $required"
        }
    }

    $snapshotStart = $installerText.IndexOf(
        'private static UpdateRollbackTransaction CreateUpdateRollbackTransaction()',
        [StringComparison]::Ordinal)
    $preparingState = $installerText.IndexOf(
        'WriteUpdateState(transaction);',
        $snapshotStart,
        [StringComparison]::Ordinal)
    $watchdogStart = $installerText.IndexOf(
        'StartUpdateRecoveryWatchdog(transaction);',
        $preparingState,
        [StringComparison]::Ordinal)
    $snapshotCopy = $installerText.IndexOf(
        'CreateFileSnapshot(',
        $watchdogStart,
        [StringComparison]::Ordinal)
    $preparedState = $installerText.IndexOf(
        'UpdateRollbackStatus.Prepared',
        $snapshotCopy,
        [StringComparison]::Ordinal)
    if ($snapshotStart -lt 0 -or
        $preparingState -lt $snapshotStart -or
        $watchdogStart -lt $preparingState -or
        $snapshotCopy -lt $watchdogStart -or
        $preparedState -lt $snapshotCopy) {
        throw 'The crash watchdog is not active throughout snapshot preparation.'
    }

    Write-Host 'Update rollback safety regression test passed.' `
        -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedRoot = [IO.Path]::GetFullPath($temporaryRoot)
        $tempRoot = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\')
        $expectedPrefix = $tempRoot +
            '\MajesticBoost-UpdateRollback-'
        if ($resolvedRoot.StartsWith(
                $expectedPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
        }
    }
}
