[CmdletBinding()]
param(
    [string]$ApplicationPath,
    [string]$InstallerPath
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
if (-not $InstallerPath) {
    $InstallerPath = Join-Path $projectRoot 'dist\MajesticBoost-Setup-1.6.0.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$testRoot = Join-Path $env:TEMP ('MajesticBoost-UpdaterValidation-Test-' + [Guid]::NewGuid().ToString('N'))
$fixturePath = Join-Path $testRoot (Split-Path -Leaf $InstallerPath)

function Get-ChildWriterResult {
    param([Parameter(Mandatory = $true)][string]$Path)

    $command = @'
$ErrorActionPreference = 'Stop'
try {
    $stream = [IO.File]::Open(
        $env:MAJESTICBOOST_VALIDATION_FIXTURE,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Write,
        [IO.FileShare]::ReadWrite)
    $stream.Dispose()
    [Console]::Write('OPENED')
}
catch [IO.IOException] {
    [Console]::Write('BLOCKED')
}
'@
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $startInfo.Arguments = '-NoProfile -NonInteractive -EncodedCommand ' + $encodedCommand
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables['MAJESTICBOOST_VALIDATION_FIXTURE'] = $Path
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        if (-not $process.WaitForExit(10000)) {
            $process.Kill()
            throw 'Timed out waiting for the cross-process writer probe.'
        }
        $output = $process.StandardOutput.ReadToEnd()
        $errorOutput = $process.StandardError.ReadToEnd()
        if ($process.ExitCode -ne 0) {
            throw "Cross-process writer probe failed: $errorOutput"
        }
        return $output
    }
    finally {
        $process.Dispose()
    }
}

$sourcePath = Join-Path $projectRoot 'MajesticBoost\UpdateFlow.cs'
$source = [IO.File]::ReadAllText($sourcePath)
$downloadClose = $source.IndexOf('using (var downloadStream = new FileStream(', [StringComparison]::Ordinal)
$verificationOpen = $source.IndexOf('using (FileStream verificationStream = OpenInstallerForVerification(installerPath))', [StringComparison]::Ordinal)
if ($downloadClose -lt 0 -or $verificationOpen -le $downloadClose) {
    throw 'The updater does not close the write-capable download stream before verification.'
}

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ApplicationPath))
$overlayType = $assembly.GetType('MajesticBoost.UpdateFlowOverlay', $true, $false)
$staticFlags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
$nestedFlags = [Reflection.BindingFlags]::NonPublic
$openMethod = $overlayType.GetMethod('OpenInstallerForVerification', $staticFlags)
$validateMethod = $overlayType.GetMethod('ValidateHeldInstaller', $staticFlags)
$refreshMethod = $overlayType.GetMethod('RefreshAvailableUpdateAsync', [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance)
$lockMethod = $overlayType.GetMethod('TryAcquireUpdateLock', $staticFlags)
if (-not $openMethod -or -not $validateMethod -or -not $refreshMethod -or -not $lockMethod) {
    throw 'Compiled updater validation helpers were not found.'
}

$manifestType = $overlayType.GetNestedType('UpdateManifest', $nestedFlags)
$semanticType = $overlayType.GetNestedType('SemanticVersion', $nestedFlags)
if (-not $manifestType -or -not $semanticType) {
    throw 'Compiled updater manifest types were not found.'
}

$verificationStream = $null
try {
    [void](New-Item -ItemType Directory -Path $testRoot)
    Copy-Item -LiteralPath $InstallerPath -Destination $fixturePath

    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($fixturePath)
    $fileVersion = [Version]::Parse($versionInfo.FileVersion)
    $semanticVersion = [Activator]::CreateInstance($semanticType)
    $semanticType.GetField('Major').SetValue($semanticVersion, $fileVersion.Major)
    $semanticType.GetField('Minor').SetValue($semanticVersion, $fileVersion.Minor)
    $semanticType.GetField('Patch').SetValue($semanticVersion, $fileVersion.Build)

    $manifest = [Activator]::CreateInstance($manifestType, $true)
    $manifestType.GetField('Version').SetValue($manifest, $semanticVersion)
    $manifestType.GetField('InstallerUrl').SetValue($manifest, 'https://example.invalid/setup.exe')
    $manifestType.GetField('Sha256').SetValue(
        $manifest,
        (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash)
    $manifestType.GetField('Size').SetValue($manifest, [long](Get-Item -LiteralPath $fixturePath).Length)

    $openArguments = New-Object 'object[]' 1
    $openArguments[0] = [string]$fixturePath
    $verificationStream = [IO.FileStream]$openMethod.Invoke($null, $openArguments)

    if ($verificationStream.CanWrite) {
        throw 'The verification handle is unexpectedly write-capable.'
    }

    $heldVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($fixturePath)
    if ($heldVersionInfo.ProductName -cne 'Majestic Boost' -or
        [string]::IsNullOrWhiteSpace($heldVersionInfo.FileVersion)) {
        throw 'Windows version metadata is not readable while the verification handle is held.'
    }

    if ((Get-ChildWriterResult -Path $fixturePath) -cne 'BLOCKED') {
        throw 'A second writer could open the installer during final verification.'
    }

    $validateArguments = New-Object 'object[]' 3
    $validateArguments[0] = $manifest
    $validateArguments[1] = [string]$fixturePath
    $validateArguments[2] = $verificationStream
    try {
        [void]$validateMethod.Invoke($null, $validateArguments)
    }
    catch [Reflection.TargetInvocationException] {
        throw $_.Exception.InnerException
    }
}
finally {
    if ($verificationStream) {
        $verificationStream.Dispose()
    }
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

foreach ($requiredText in @(
    'state == UpdateState.Retry && !demoMode',
    'RefreshAvailableUpdateAsync()',
    'update-operation.lock',
    'FileOptions.DeleteOnClose',
    'catch (InvalidDataException ex)',
    'catch (WebException ex)'
)) {
    if (-not $source.Contains($requiredText)) {
        throw "Updater retry/resilience policy is missing: $requiredText"
    }
}

Write-Host 'Updater installer validation regression test passed.' -ForegroundColor Green
