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
    $InstallerPath = Join-Path $projectRoot 'dist\MajesticBoost-Setup-1.6.3.exe'
}
$ApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path

$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($ApplicationPath))
if ($assembly.GetName().Version.ToString() -cne '1.6.3.0') {
    throw 'Compiled application version is not 1.6.3.0.'
}
$company = @(
    $assembly.GetCustomAttributes(
        [Reflection.AssemblyCompanyAttribute],
        $false)
) | Select-Object -First 1
if (-not $company -or $company.Company -cne 'Silus Suspect') {
    throw 'Compiled application author metadata is not Silus Suspect.'
}

$overlayType = $assembly.GetType('MajesticBoost.UpdateFlowOverlay', $true, $false)
$flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
$manifestUrl = [string]$overlayType.GetField('ManifestUrl', $flags).GetRawConstantValue()
$signatureUrl = [string]$overlayType.GetField('ManifestSignatureUrl', $flags).GetRawConstantValue()
if (-not $manifestUrl.EndsWith('/update-v2.json', [StringComparison]::Ordinal) -or
    -not $signatureUrl.EndsWith('/update-v2.json.sig', [StringComparison]::Ordinal)) {
    throw 'Compiled 1.6.3 application is not pinned to the v2 update channel.'
}

$decodeMethod = $overlayType.GetMethod('DecodeManifestSignature', $flags)
$verifyMethod = $overlayType.GetMethod('VerifyManifestSignature', $flags)
if (-not $decodeMethod -or -not $verifyMethod) {
    throw 'Compiled manifest verification methods were not found.'
}

function Test-SignedManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$SignaturePath
    )

    $manifestBytes = [IO.File]::ReadAllBytes($ManifestPath)
    $signaturePayload = [IO.File]::ReadAllBytes($SignaturePath)
    try {
        $decodeArguments = New-Object 'object[]' 1
        $decodeArguments[0] = $signaturePayload
        $signatureBytes = [byte[]]$decodeMethod.Invoke($null, $decodeArguments)
        $verifyArguments = New-Object 'object[]' 2
        $verifyArguments[0] = $manifestBytes
        $verifyArguments[1] = $signatureBytes
        [void]$verifyMethod.Invoke($null, $verifyArguments)
    }
    catch [Reflection.TargetInvocationException] {
        throw $_.Exception.InnerException
    }
}

$legacyManifestPath = Join-Path $projectRoot 'update.json'
$legacySignaturePath = Join-Path $projectRoot 'update.json.sig'
$v2ManifestPath = Join-Path $projectRoot 'update-v2.json'
$v2SignaturePath = Join-Path $projectRoot 'update-v2.json.sig'
Test-SignedManifest -ManifestPath $legacyManifestPath -SignaturePath $legacySignaturePath
Test-SignedManifest -ManifestPath $v2ManifestPath -SignaturePath $v2SignaturePath

$legacy = Get-Content -Raw -Encoding UTF8 $legacyManifestPath | ConvertFrom-Json
if ([string]$legacy.version -cne '0.0.0' -or [long]$legacy.size -ne 1L -or
    [string]$legacy.sha256 -cne ('0' * 64)) {
    throw 'Legacy update channel is not safely frozen below every released application version.'
}

$v2 = Get-Content -Raw -Encoding UTF8 $v2ManifestPath | ConvertFrom-Json
$installer = Get-Item -LiteralPath $InstallerPath
$installerHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash
if ([string]$v2.version -cne '1.6.3' -or
    [long]$v2.size -ne [long]$installer.Length -or
    [string]$v2.sha256 -cne $installerHash -or
    [string]$v2.installerUrl -cne 'https://raw.githubusercontent.com/alosev394-ai/MajesticBoost/main/dist/MajesticBoost-Setup-1.6.3.exe') {
    throw 'V2 manifest does not exactly describe the release installer.'
}

Write-Host 'Release update channel regression test passed.' -ForegroundColor Green
