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
    $InstallerPath = Join-Path $projectRoot 'dist\MajesticBoost-Setup-1.6.3.exe'
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
if ($versionInfo.ProductName -cne 'Majestic Boost' -or
    $versionInfo.FileVersion -cne '1.6.3.0' -or
    $versionInfo.CompanyName -cne 'Silus Suspect') {
    throw 'Installer product metadata does not match release 1.6.3.'
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

Test-DowngradeDecision -Installed '1.5.1.0' -Setup '1.6.3.0' -Expected $false
Test-DowngradeDecision -Installed '1.6.1.0' -Setup '1.6.3.0' -Expected $false
Test-DowngradeDecision -Installed '1.6.2.0' -Setup '1.6.3.0' -Expected $false
Test-DowngradeDecision -Installed '1.6.3.0' -Setup '1.6.3.0' -Expected $false
Test-DowngradeDecision -Installed '1.6.4.0' -Setup '1.6.3.0' -Expected $true
Test-DowngradeDecision -Installed '2.0.0.0' -Setup '1.6.3.0' -Expected $true
Test-DowngradeDecision -Installed 'invalid' -Setup '1.6.3.0' -Expected $false

$payloadStream = $assembly.GetManifestResourceStream('MajesticBoost.Payload.exe')
if (-not $payloadStream) {
    throw 'Embedded MajesticBoost payload is missing.'
}
try {
    $memory = New-Object IO.MemoryStream
    try {
        $payloadStream.CopyTo($memory)
        $payloadAssembly = [Reflection.Assembly]::Load($memory.ToArray())
        if ($payloadAssembly.GetName().Version.ToString() -cne '1.6.3.0') {
            throw 'Embedded application version does not match installer version 1.6.3.'
        }
        $payloadCompany = @(
            $payloadAssembly.GetCustomAttributes(
                [Reflection.AssemblyCompanyAttribute],
                $false)
        ) | Select-Object -First 1
        if (-not $payloadCompany -or $payloadCompany.Company -cne 'Silus Suspect') {
            throw 'Embedded application author metadata is not Silus Suspect.'
        }
    }
    finally {
        $memory.Dispose()
    }
}
finally {
    $payloadStream.Dispose()
}

$presentMonStream = $assembly.GetManifestResourceStream('MajesticBoost.PresentMon.exe')
if (-not $presentMonStream) {
    throw 'Embedded PresentMon payload is missing.'
}
try {
    if ($presentMonStream.Length -ne 956768) {
        throw 'Embedded PresentMon payload has the wrong length.'
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = ([BitConverter]::ToString($sha.ComputeHash($presentMonStream))).Replace('-', '').ToLowerInvariant()
        if ($hash -cne '9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191') {
            throw 'Embedded PresentMon payload has the wrong SHA-256.'
        }
    }
    finally {
        $sha.Dispose()
    }
}
finally {
    $presentMonStream.Dispose()
}
foreach ($noticeName in @(
    'MajesticBoost.PresentMon.License.txt',
    'MajesticBoost.PresentMon.ThirdParty.txt'
)) {
    $notice = $assembly.GetManifestResourceStream($noticeName)
    if (-not $notice -or $notice.Length -eq 0) {
        throw "Embedded PresentMon notice is missing: $noticeName"
    }
    $notice.Dispose()
}

$source = [IO.File]::ReadAllText((Join-Path $projectRoot 'MajesticBoostInstaller\Program.cs'))
$validationLoop = $source.IndexOf('ValidateStagedPayload(item.StagePath, item.Executable);', [StringComparison]::Ordinal)
$stopInstalledApp = $source.IndexOf('StopInstalledApplication();', $validationLoop, [StringComparison]::Ordinal)
$commitLoop = $source.IndexOf('CommitStagedFile(', $stopInstalledApp, [StringComparison]::Ordinal)
if ($validationLoop -lt 0 -or $stopInstalledApp -le $validationLoop -or
    $commitLoop -le $stopInstalledApp) {
    throw 'Installer must validate every payload before stopping the old app and committing files.'
}
$registrationCapture = $source.IndexOf(
    'CapturePostInstallRegistration();',
    [StringComparison]::Ordinal)
$registrationCommit = $source.IndexOf(
    'registerInstallation();',
    $commitLoop,
    [StringComparison]::Ordinal)
$transactionSuccess = $source.IndexOf(
    'installationSucceeded = true;',
    $registrationCommit,
    [StringComparison]::Ordinal)
$registrationCompensation = $source.IndexOf(
    'RestorePostInstallRegistration(registrationSnapshot);',
    $registrationCapture,
    [StringComparison]::Ordinal)
if ($registrationCapture -lt 0 -or $registrationCompensation -le $registrationCapture -or
    $registrationCommit -le $commitLoop -or $transactionSuccess -le $registrationCommit) {
    throw 'Shortcuts and registry registration must participate in the payload transaction with compensation.'
}
if ($source.Contains('File.Copy(Application.ExecutablePath, UninstallerExe, true)')) {
    throw 'Uninstall.exe is still published outside the payload transaction.'
}
foreach ($requiredText in @(
    'AssemblyCompany("Silus Suspect")',
    'uninstall.SetValue("Publisher", "Silus Suspect", RegistryValueKind.String)',
    'ValidatePresentMonPayload(item.StagePath)',
    'TryDeleteIfExists(item.StagePath)',
    'MajesticBoost.PresentMon.exe',
    'items.Count - 1; index >= 0; index--',
    'GetDesktopShortcutPreference()',
    'ScheduleUpdateSourceCleanupIfNeeded()',
    'CaptureRegistryTree(child, childName)',
    'RestoreRegistryKey(baseKey, snapshot.AppPathsKey)',
    'RestoreRegistryKey(baseKey, snapshot.UninstallKey)',
    'RestoreShortcut(snapshot.DesktopShortcut)',
    'RestoreShortcut(snapshot.StartMenuShortcut)',
    '^MajesticBoost\.Update\.[0-9a-f]{32}$'
)) {
    if (-not $source.Contains($requiredText)) {
        throw "Installer resilience policy is missing: $requiredText"
    }
}

Write-Host 'Installer compatibility regression test passed.' -ForegroundColor Green
