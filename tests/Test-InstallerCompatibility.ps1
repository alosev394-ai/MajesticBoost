[CmdletBinding()]
param(
    [string]$InstallerPath,
    [string]$LatestInstallerPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $InstallerPath) {
    $InstallerPath = Join-Path $projectRoot 'dist\MajesticBoost-Setup-1.5.1.exe'
}
if (-not $LatestInstallerPath) {
    $LatestInstallerPath = Join-Path $projectRoot 'dist\MajesticBoost-Setup-Latest.exe'
}
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$LatestInstallerPath = (Resolve-Path -LiteralPath $LatestInstallerPath).Path

$installerHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash
$latestHash = (Get-FileHash -LiteralPath $LatestInstallerPath -Algorithm SHA256).Hash
if ($installerHash -cne $latestHash) {
    throw 'The stable Latest setup is not byte-identical to the versioned setup.'
}

$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($InstallerPath)
if ($versionInfo.ProductName -cne 'Majestic Boost' -or $versionInfo.FileVersion -cne '1.5.1.0') {
    throw 'Installer product metadata does not match release 1.5.1.'
}

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($InstallerPath))
$engineType = $assembly.GetType('MajesticBoostSetup.InstallerEngine', $true, $false)
$programType = $assembly.GetType('MajesticBoostSetup.Program', $true, $false)
$flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
$downgradeMethod = $engineType.GetMethod('IsDowngrade', $flags)
if (-not $downgradeMethod) {
    throw 'Compiled installer downgrade guard was not found.'
}
$mutexName = [string]$programType.GetField('SetupMutexName', $flags).GetRawConstantValue()
if ($mutexName -cne 'Global\CodexGamingOptimization.MajesticBoost.Setup') {
    throw 'Installer does not use the expected global install/uninstall mutex.'
}

function Test-DowngradeDecision {
    param([string]$Installed, [string]$Setup, [bool]$Expected)

    $arguments = New-Object 'object[]' 2
    $arguments[0] = $Installed
    $arguments[1] = $Setup
    $actual = [bool]$downgradeMethod.Invoke($null, $arguments)
    if ($actual -ne $Expected) {
        throw "Downgrade decision for installed=$Installed setup=$Setup was $actual; expected $Expected."
    }
}

Test-DowngradeDecision -Installed '1.4.1.0' -Setup '1.5.1.0' -Expected $false
Test-DowngradeDecision -Installed '1.5.1.0' -Setup '1.5.1.0' -Expected $false
Test-DowngradeDecision -Installed '1.5.2.0' -Setup '1.5.1.0' -Expected $true
Test-DowngradeDecision -Installed '2.0.0.0' -Setup '1.5.1.0' -Expected $true
Test-DowngradeDecision -Installed 'invalid' -Setup '1.5.1.0' -Expected $false

$payloadStream = $assembly.GetManifestResourceStream('MajesticBoost.Payload.exe')
if (-not $payloadStream) {
    throw 'Embedded MajesticBoost payload is missing.'
}
try {
    $memory = New-Object IO.MemoryStream
    try {
        $payloadStream.CopyTo($memory)
        $payloadAssembly = [Reflection.Assembly]::Load($memory.ToArray())
        if ($payloadAssembly.GetName().Version.ToString() -cne '1.5.1.0') {
            throw 'Embedded application version does not match installer version 1.5.1.'
        }
    }
    finally {
        $memory.Dispose()
    }
}
finally {
    $payloadStream.Dispose()
}

$source = [IO.File]::ReadAllText((Join-Path $projectRoot 'MajesticBoostInstaller\Program.cs'))
$lastPayloadValidation = $source.IndexOf('ValidateStagedPayload(maxFpsRestoreStage, false);', [StringComparison]::Ordinal)
$stopInstalledApp = $source.IndexOf('StopInstalledApplication();', $lastPayloadValidation, [StringComparison]::Ordinal)
$uninstallerCommit = $source.IndexOf('CommitStagedFile(uninstallerStage, UninstallerExe,', $lastPayloadValidation, [StringComparison]::Ordinal)
$firstCommit = $source.IndexOf('CommitStagedFile(gameBoostStage,', $lastPayloadValidation, [StringComparison]::Ordinal)
if ($lastPayloadValidation -lt 0 -or $stopInstalledApp -le $lastPayloadValidation -or
    $firstCommit -le $stopInstalledApp -or $uninstallerCommit -le $firstCommit) {
    throw 'Installer must validate every payload before stopping the old app and committing files.'
}
if ($source.Contains('File.Copy(Application.ExecutablePath, UninstallerExe, true)')) {
    throw 'Uninstall.exe is still published outside the payload transaction.'
}
foreach ($requiredText in @(
    'ValidateStagedPayload(uninstallerStage, true)',
    'TryDeleteIfExists(appStage)',
    'GetDesktopShortcutPreference()',
    'ScheduleUpdateSourceCleanupIfNeeded()',
    '^MajesticBoost\.Update\.[0-9a-f]{32}$'
)) {
    if (-not $source.Contains($requiredText)) {
        throw "Installer resilience policy is missing: $requiredText"
    }
}

Write-Host 'Installer compatibility regression test passed.' -ForegroundColor Green
