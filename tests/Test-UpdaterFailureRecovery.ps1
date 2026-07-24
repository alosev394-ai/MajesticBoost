[CmdletBinding()]
param(
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or
    $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ApplicationPath)) {
    $ApplicationPath = Join-Path $projectRoot 'dist\MajesticBoost.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
$sourcePath = Join-Path $projectRoot 'MajesticBoost\UpdateFlow.cs'
$source = [IO.File]::ReadAllText($sourcePath)

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ApplicationPath))
$overlayType = $assembly.GetType(
    'MajesticBoost.UpdateFlowOverlay',
    $true,
    $false)
$staticFlags =
    [Reflection.BindingFlags]::NonPublic -bor
    [Reflection.BindingFlags]::Static

function Get-RequiredStaticMethod {
    param([Parameter(Mandatory = $true)][string]$Name)

    $method = $overlayType.GetMethod($Name, $staticFlags)
    if (-not $method) {
        throw "Compiled updater method was not found: $Name"
    }
    return $method
}

function Get-RequiredStaticConstant {
    param([Parameter(Mandatory = $true)][string]$Name)

    $field = $overlayType.GetField($Name, $staticFlags)
    if (-not $field -or -not $field.IsLiteral) {
        throw "Compiled updater constant was not found: $Name"
    }
    return $field.GetRawConstantValue()
}

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

function Invoke-StaticMethod {
    param(
        [Parameter(Mandatory = $true)][Reflection.MethodInfo]$Method,
        [Parameter(Mandatory = $true)][object[]]$Arguments
    )

    try {
        return $Method.Invoke($null, $Arguments)
    }
    catch {
        throw (Get-InnerException -Exception $_.Exception)
    }
}

$fallbackDecisionMethod = Get-RequiredStaticMethod `
    -Name 'ShouldUseRepositoryRefFallback'
$calculateRequestTimeoutMethod = Get-RequiredStaticMethod `
    -Name 'CalculateManifestRequestTimeout'
$buildMainAddressMethod = Get-RequiredStaticMethod `
    -Name 'BuildSignedMainManifestAddress'
$verifyAndParseMethod = Get-RequiredStaticMethod `
    -Name 'VerifyAndParseManifest'
$validateStateDirectoryMethod = Get-RequiredStaticMethod `
    -Name 'ValidateUpdateStateDirectory'
$lockContentionMethod = Get-RequiredStaticMethod `
    -Name 'IsUpdateLockContention'

$maximumAttempts = [int](Get-RequiredStaticConstant `
    -Name 'ManifestFetchAttempts')
if ($maximumAttempts -ne 3) {
    throw 'The fallback scenario matrix expects three repository attempts.'
}
$requestTimeout = [int](Get-RequiredStaticConstant `
    -Name 'ManifestRequestTimeoutMilliseconds')
$repositoryBudget = [int](Get-RequiredStaticConstant `
    -Name 'ManifestTotalTimeoutMilliseconds')
$fallbackBudget = [int](Get-RequiredStaticConstant `
    -Name 'ManifestFallbackTotalTimeoutMilliseconds')
$retryDelay = [int](Get-RequiredStaticConstant `
    -Name 'ManifestRetryDelayMilliseconds')

$minimumFallbackBudget = (2 * $requestTimeout) + 500
if ($fallbackBudget -lt $minimumFallbackBudget) {
    throw (
        'The signed-main fallback budget cannot accommodate two bounded ' +
        'downloads plus the completion reserve.')
}

$repositoryExhaustionElapsed = $maximumAttempts * $requestTimeout
foreach ($attempt in 1..($maximumAttempts - 1)) {
    $repositoryExhaustionElapsed += $retryDelay * $attempt
}
$sharedBudgetRemaining = $repositoryBudget - $repositoryExhaustionElapsed
if ($sharedBudgetRemaining -ge $minimumFallbackBudget) {
    throw 'The fallback-budget regression scenario no longer exercises exhaustion.'
}

$budgetScenarios = @(
    [pscustomobject]@{
        Elapsed = 0L
        Expected = $requestTimeout
        Label = 'first fallback download'
    },
    [pscustomobject]@{
        Elapsed = [long]$requestTimeout
        Expected = $requestTimeout
        Label = 'second fallback download'
    },
    [pscustomobject]@{
        Elapsed = [long](2 * $requestTimeout)
        Expected = $fallbackBudget - (2 * $requestTimeout)
        Label = 'completion reserve'
    }
)
foreach ($scenario in $budgetScenarios) {
    $arguments = New-Object 'object[]' 2
    $arguments[0] = [long]$scenario.Elapsed
    $arguments[1] = $fallbackBudget
    $actual = [int](Invoke-StaticMethod `
        -Method $calculateRequestTimeoutMethod `
        -Arguments $arguments)
    if ($actual -ne [int]$scenario.Expected) {
        throw (
            "Unexpected fallback request budget for {0}: expected {1}, got {2}." -f
            $scenario.Label,
            $scenario.Expected,
            $actual)
    }
}

$arguments = New-Object 'object[]' 2
$arguments[0] = [long]($fallbackBudget - 499)
$arguments[1] = $fallbackBudget
try {
    [void](Invoke-StaticMethod `
        -Method $calculateRequestTimeoutMethod `
        -Arguments $arguments)
    throw 'The signed-main fallback exceeded its independent total budget.'
}
catch {
    $failure = Get-InnerException -Exception $_.Exception
    if ($failure -isnot [Net.WebException] -or
        $failure.Status -ne [Net.WebExceptionStatus]::Timeout) {
        throw $failure
    }
}

$fallbackScenarios = @(
    [pscustomobject]@{
        Status = [Net.WebExceptionStatus]::ProtocolError
        Http = 403
        ExpectedByAttempt = @($true, $true, $true)
        Label = 'GitHub API unauthenticated rate-limit 403'
    },
    [pscustomobject]@{
        Status = [Net.WebExceptionStatus]::ProtocolError
        Http = 429
        ExpectedByAttempt = @($true, $true, $true)
        Label = 'GitHub API 429'
    },
    [pscustomobject]@{
        Status = [Net.WebExceptionStatus]::NameResolutionFailure
        Http = 0
        ExpectedByAttempt = @($true, $true, $true)
        Label = 'API DNS'
    },
    [pscustomobject]@{
        Status = [Net.WebExceptionStatus]::ProxyNameResolutionFailure
        Http = 0
        ExpectedByAttempt = @($true, $true, $true)
        Label = 'proxy DNS'
    },
    [pscustomobject]@{
        Status = [Net.WebExceptionStatus]::ProtocolError
        Http = 404
        ExpectedByAttempt = @($false, $false, $false)
        Label = 'GitHub API 404'
    },
    [pscustomobject]@{
        Status = [Net.WebExceptionStatus]::ProtocolError
        Http = 500
        ExpectedByAttempt = @($false, $false, $true)
        Label = 'GitHub API 500'
    },
    [pscustomobject]@{
        Status = [Net.WebExceptionStatus]::TrustFailure
        Http = 0
        ExpectedByAttempt = @($false, $false, $false)
        Label = 'TLS trust failure'
    },
    [pscustomobject]@{
        Status = [Net.WebExceptionStatus]::SecureChannelFailure
        Http = 0
        ExpectedByAttempt = @($false, $false, $false)
        Label = 'TLS secure-channel failure'
    },
    [pscustomobject]@{
        Status = [Net.WebExceptionStatus]::ProtocolError
        Http = 401
        ExpectedByAttempt = @($false, $false, $false)
        Label = 'GitHub API authentication 401'
    },
    [pscustomobject]@{
        Status = [Net.WebExceptionStatus]::ProtocolError
        Http = 407
        ExpectedByAttempt = @($false, $false, $false)
        Label = 'proxy authentication 407'
    },
    [pscustomobject]@{
        Status = [Net.WebExceptionStatus]::Timeout
        Http = 0
        ExpectedByAttempt = @($false, $false, $true)
        Label = 'API timeout'
    },
    [pscustomobject]@{
        Status = [Net.WebExceptionStatus]::ConnectFailure
        Http = 0
        ExpectedByAttempt = @($false, $false, $true)
        Label = 'API connect failure'
    },
    [pscustomobject]@{
        Status = [Net.WebExceptionStatus]::SendFailure
        Http = 0
        ExpectedByAttempt = @($false, $false, $true)
        Label = 'API send failure'
    },
    [pscustomobject]@{
        Status = [Net.WebExceptionStatus]::ReceiveFailure
        Http = 0
        ExpectedByAttempt = @($false, $false, $true)
        Label = 'API receive failure'
    }
)
foreach ($scenario in $fallbackScenarios) {
    if ($scenario.ExpectedByAttempt.Count -ne $maximumAttempts) {
        throw "Invalid fallback test scenario: $($scenario.Label)."
    }
    foreach ($attempt in 1..$maximumAttempts) {
        $arguments = New-Object 'object[]' 4
        $arguments[0] = $scenario.Status
        $arguments[1] = [int]$scenario.Http
        $arguments[2] = $attempt
        $arguments[3] = $maximumAttempts
        $actual = [bool](Invoke-StaticMethod `
            -Method $fallbackDecisionMethod `
            -Arguments $arguments)
        $expected = [bool]$scenario.ExpectedByAttempt[$attempt - 1]
        if ($actual -ne $expected) {
            throw (
                "Unexpected signed-main fallback decision for {0} " +
                "on attempt {1}: expected {2}, got {3}." -f
                $scenario.Label,
                $attempt,
                $expected,
                $actual)
        }
    }
}

$addressArguments = New-Object 'object[]' 1
$addressArguments[0] = 'update-v2.json'
$manifestAddress = [string](Invoke-StaticMethod `
    -Method $buildMainAddressMethod `
    -Arguments $addressArguments)
$addressArguments[0] = 'update-v2.json.sig'
$signatureAddress = [string](Invoke-StaticMethod `
    -Method $buildMainAddressMethod `
    -Arguments $addressArguments)
if ($manifestAddress -cne
        'https://raw.githubusercontent.com/alosev394-ai/MajesticBoost/main/update-v2.json' -or
    $signatureAddress -cne
        'https://raw.githubusercontent.com/alosev394-ai/MajesticBoost/main/update-v2.json.sig') {
    throw 'The signed-main fallback URLs are not the exact trusted release paths.'
}

$addressArguments[0] = '..\update-v2.json'
try {
    [void](Invoke-StaticMethod `
        -Method $buildMainAddressMethod `
        -Arguments $addressArguments)
    throw 'The signed-main URL builder accepted an untrusted file name.'
}
catch {
    $failure = Get-InnerException -Exception $_.Exception
    if ($failure -isnot [IO.InvalidDataException]) {
        throw $failure
    }
}

$manifestBytes = [IO.File]::ReadAllBytes(
    (Join-Path $projectRoot 'update-v2.json'))
$signatureBytes = [IO.File]::ReadAllBytes(
    (Join-Path $projectRoot 'update-v2.json.sig'))
$verificationArguments = New-Object 'object[]' 2
$verificationArguments[0] = $manifestBytes
$verificationArguments[1] = $signatureBytes
$manifest = Invoke-StaticMethod `
    -Method $verifyAndParseMethod `
    -Arguments $verificationArguments
if (-not $manifest) {
    throw 'The repository signed manifest did not validate.'
}

[byte[]]$tamperedManifest = $manifestBytes.Clone()
$tamperedManifest[0] = $tamperedManifest[0] -bxor 1
$verificationArguments[0] = $tamperedManifest
try {
    [void](Invoke-StaticMethod `
        -Method $verifyAndParseMethod `
        -Arguments $verificationArguments)
    throw 'A tampered fallback manifest passed RSA verification.'
}
catch {
    $failure = Get-InnerException -Exception $_.Exception
    if ($failure -isnot [Security.Cryptography.CryptographicException]) {
        throw $failure
    }
}

$stateArguments = New-Object 'object[]' 1
$stateArguments[0] = [IO.Path]::GetTempPath()
$validatedStateDirectory = [string](Invoke-StaticMethod `
    -Method $validateStateDirectoryMethod `
    -Arguments $stateArguments)
$expectedStateDirectory = Join-Path `
    ([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) `
    'MajesticBoost'
if ($validatedStateDirectory -cne $expectedStateDirectory) {
    throw 'The update-lock directory validator returned an unexpected path.'
}

foreach ($invalidRoot in @('', 'relative\profile')) {
    $stateArguments[0] = $invalidRoot
    try {
        [void](Invoke-StaticMethod `
            -Method $validateStateDirectoryMethod `
            -Arguments $stateArguments)
        throw "Invalid LocalApplicationData was accepted: $invalidRoot"
    }
    catch {
        $failure = Get-InnerException -Exception $_.Exception
        if ($failure.GetType().FullName -cne
            'MajesticBoost.UpdateFlowOverlay+UpdateStorageException') {
            throw $failure
        }
    }
}

$lockArguments = New-Object 'object[]' 1
$lockArguments[0] = [IO.IOException]::new(
    'sharing violation',
    -2147024864)
if (-not [bool](Invoke-StaticMethod `
        -Method $lockContentionMethod `
        -Arguments $lockArguments)) {
    throw 'A Windows sharing violation is not classified as lock contention.'
}
$lockArguments[0] = [IO.IOException]::new(
    'disk full',
    -2147024784)
if ([bool](Invoke-StaticMethod `
        -Method $lockContentionMethod `
        -Arguments $lockArguments)) {
    throw 'A storage failure was incorrectly classified as another updater instance.'
}

$fetchStart = $source.IndexOf(
    'private UpdateManifest FetchAndValidateManifest()',
    [StringComparison]::Ordinal)
$fallbackEligibility = $source.IndexOf(
    'IsRepositoryRefFallbackEligible(ex, attempt)',
    $fetchStart,
    [StringComparison]::Ordinal)
$transientRetry = $source.IndexOf(
    'if (!IsTransientManifestFailure(ex)',
    $fallbackEligibility,
    [StringComparison]::Ordinal)
$verifyWrapperStart = $source.IndexOf(
    'private static UpdateManifest VerifyAndParseManifest(',
    [StringComparison]::Ordinal)
$decodeIndex = $source.IndexOf(
    'DecodeManifestSignature(signaturePayload)',
    $verifyWrapperStart,
    [StringComparison]::Ordinal)
$verifyIndex = $source.IndexOf(
    'VerifyManifestSignature(payload, signature)',
    $decodeIndex,
    [StringComparison]::Ordinal)
$parseIndex = $source.IndexOf(
    'ParseAndValidateManifest(payload)',
    $verifyIndex,
    [StringComparison]::Ordinal)
if ($fetchStart -lt 0 -or
    $fallbackEligibility -lt $fetchStart -or
    $transientRetry -lt $fallbackEligibility -or
    $verifyWrapperStart -lt 0 -or
    $decodeIndex -lt $verifyWrapperStart -or
    $verifyIndex -lt $decodeIndex -or
    $parseIndex -lt $verifyIndex) {
    throw 'The fallback does not preserve signature-before-parse ordering.'
}

foreach ($requiredText in @(
    '!repositoryHeadResolved',
    'Stopwatch fallbackTimer = Stopwatch.StartNew();',
    'ManifestFallbackTotalTimeoutMilliseconds',
    'BuildSignedMainManifestAddress("update-v2.json")',
    'BuildSignedMainManifestAddress("update-v2.json.sig")',
    'VerifyAndParseManifest(',
    'AllowAutoRedirect = false',
    'Unexpected final response URL.',
    'UpdateCheckDiagnostic',
    'UpdateCheckRetryRecommended',
    'catch (UpdateStorageException ex)',
    'catch (UnauthorizedAccessException ex)',
    'catch (SecurityException ex)',
    'FileAttributes.ReparsePoint',
    'ShowUpdateStorageRetry(ex)'
)) {
    if (-not $source.Contains($requiredText)) {
        throw "Updater failure-recovery contract is missing: $requiredText"
    }
}
if ($source.Contains('ServerCertificateValidationCallback')) {
    throw 'The updater overrides Windows TLS certificate validation.'
}

Write-Host 'Updater failure-recovery regression test passed.' `
    -ForegroundColor Green
