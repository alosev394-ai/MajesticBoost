[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$flowPath = Join-Path $projectRoot 'MajesticBoost\OptimizationFlow.cs'
$applyPath = Join-Path $projectRoot 'outputs\MaxFPS-Apply.ps1'
$restorePath = Join-Path $projectRoot 'outputs\MaxFPS-Restore.ps1'
$flow = [IO.File]::ReadAllText($flowPath)

foreach ($required in @(
    'ElevatedScriptTimeoutMilliseconds',
    'OpenVerifiedScript(',
    'FileShare.Read',
    'SHA256.Create()',
    'WaitForProcessExitSafely(',
    'while (!process.WaitForExit(pollMilliseconds))',
    'process.Kill()',
    '-ExpectedUserSid ',
    'WindowsIdentity.GetCurrent()'
)) {
    if (-not $flow.Contains($required)) {
        throw "Optimization elevation safety contract is missing: $required"
    }
}

foreach ($forbidden in @(
    'process.WaitForExit();',
    'FileShare.ReadWrite',
    'FileShare.Delete'
)) {
    if ($flow.Contains($forbidden)) {
        throw "Optimization elevation safety contains forbidden behavior: $forbidden"
    }
}

$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpfRoot = Join-Path $frameworkRoot 'WPF'
$compiler = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw ".NET Framework C# compiler not found: $compiler"
}

$hashContracts = @{
    'ApplyScriptSha256' = $applyPath
    'RestoreScriptSha256' = $restorePath
}
foreach ($entry in $hashContracts.GetEnumerator()) {
    $pattern = 'private const string ' + [Regex]::Escape($entry.Key) +
        '\s*=\s*"([0-9A-F]{64})";'
    $match = [Regex]::Match($flow, $pattern)
    if (-not $match.Success) {
        throw "Pinned script hash is missing: $($entry.Key)"
    }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $entry.Value).Hash
    if ($actual -cne $match.Groups[1].Value) {
        throw "$($entry.Key) does not match the production script."
    }
}

$testRoot = Join-Path $env:TEMP (
    'MajesticBoost-Elevation-Safety-' + [Guid]::NewGuid().ToString('N'))
$cases = @()
try {
    [void](New-Item -ItemType Directory -Path $testRoot)

    $fixtureAssembly = Join-Path $testRoot 'OptimizationFlowFixture.dll'
    $compileArguments = @(
        '/nologo',
        '/target:library',
        "/out:$fixtureAssembly",
        '/reference:System.dll',
        '/reference:System.Core.dll',
        "/reference:$frameworkRoot\System.Xaml.dll",
        "/reference:$wpfRoot\WindowsBase.dll",
        "/reference:$wpfRoot\PresentationCore.dll",
        "/reference:$wpfRoot\PresentationFramework.dll",
        $flowPath
    )
    & $compiler @compileArguments
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $fixtureAssembly)) {
        throw "Could not compile the production optimization flow fixture."
    }

    $fixture = [Reflection.Assembly]::Load(
        [IO.File]::ReadAllBytes($fixtureAssembly))
    $flowType = $fixture.GetType(
        'MajesticBoost.OptimizationFlowOverlay',
        $true,
        $false)
    $waitMethod = $flowType.GetMethod(
        'WaitForProcessExitSafely',
        [Reflection.BindingFlags]'NonPublic,Static')
    if ($null -eq $waitMethod) {
        throw 'WaitForProcessExitSafely could not be loaded from production code.'
    }

    # Exercise the exact production wait routine with a real child process and a
    # deliberately ineffective termination callback. It must retain control until
    # the child genuinely exits and writes its final marker.
    $markerPath = Join-Path $testRoot 'child-completed.marker'
    $childCommand = @"
Start-Sleep -Milliseconds 1200
[IO.File]::WriteAllText('$($markerPath.Replace("'", "''"))', 'completed')
"@
    $encodedCommand = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($childCommand))
    $child = [Diagnostics.Process]::Start(
        (Join-Path $PSHOME 'powershell.exe'),
        "-NoProfile -EncodedCommand $encodedCommand")
    if ($null -eq $child) {
        throw 'Could not start the elevated-wait scenario child process.'
    }
    try {
        $doNotTerminate = [Action[Diagnostics.Process]] {
            param([Diagnostics.Process]$ignored)
        }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $timedOut = [bool]$waitMethod.Invoke(
            $null,
            @($child, 50, 25, $doNotTerminate))
        $timer.Stop()

        if (-not $timedOut) {
            throw 'The scenario did not report the initial timeout.'
        }
        if (-not $child.HasExited) {
            throw 'The production wait routine returned while the child was alive.'
        }
        if (-not (Test-Path -LiteralPath $markerPath)) {
            throw 'The production wait routine returned before the child finalized its result.'
        }
        if ($timer.ElapsedMilliseconds -lt 800) {
            throw "The production wait routine returned too early: $($timer.ElapsedMilliseconds) ms."
        }
    }
    finally {
        if (-not $child.HasExited) {
            $child.Kill()
            [void]$child.WaitForExit(5000)
        }
        $child.Dispose()
    }

    $fakeSid = 'S-1-0-0'
    $cases = @(
        @{
            Path = $applyPath
            Result = Join-Path $env:TEMP (
                'MajesticBoost-apply-' + [Guid]::NewGuid().ToString('N') + '.json')
            Extra = @{ AdoptExistingState = $true }
        },
        @{
            Path = $restorePath
            Result = Join-Path $env:TEMP (
                'MajesticBoost-restore-' + [Guid]::NewGuid().ToString('N') + '.json')
            Extra = @{}
        }
    )

    foreach ($case in $cases) {
        $source = [IO.File]::ReadAllText($case.Path)
        foreach ($required in @(
            '[string]$ExpectedUserSid',
            '$currentUserSid.Value -ine $expectedIdentitySid.Value',
            "'-ExpectedUserSid'"
        )) {
            if (-not $source.Contains($required)) {
                throw "SID safety contract is missing from $($case.Path): $required"
            }
        }

        $arguments = @{
            ResultPath = $case.Result
            ExpectedUserSid = $fakeSid
        }
        foreach ($key in $case.Extra.Keys) {
            $arguments[$key] = $case.Extra[$key]
        }

        $failedClosed = $false
        try {
            & $case.Path @arguments
        }
        catch {
            $failedClosed = $_.Exception.Message -like '*different Windows account*'
        }
        if (-not $failedClosed) {
            throw "A mismatched elevation SID did not fail closed: $($case.Path)"
        }
        if (Test-Path -LiteralPath $case.Result) {
            throw "A mismatched elevation SID wrote a result file: $($case.Result)"
        }
    }

    'Optimization elevation identity, integrity, and timeout tests passed.'
}
finally {
    foreach ($case in $cases) {
        if (Test-Path -LiteralPath $case.Result) {
            Remove-Item -LiteralPath $case.Result -Force
        }
    }
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
