[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $false)]
    [string]$SignaturePath,

    [Parameter(Mandatory = $false)]
    [switch]$AllowLegacyChannel,

    [Parameter(Mandatory = $false)]
    [string]$PrivateKeyPath = (Join-Path $env:LOCALAPPDATA 'MajesticBoostSigning\manifest-private-v1.dpapi')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Security

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDirectory
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $projectRoot 'update-v2.json'
}
if ([string]::IsNullOrWhiteSpace($SignaturePath)) {
    $SignaturePath = Join-Path $projectRoot 'update-v2.json.sig'
}

if ((Split-Path -Leaf $ManifestPath) -ieq 'update.json' -and -not $AllowLegacyChannel) {
    throw 'The legacy update.json channel is frozen. Use update-v2.json, or pass -AllowLegacyChannel only for an intentional emergency repair.'
}

if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "Update manifest not found: $ManifestPath"
}
if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) {
    throw "Encrypted signing key not found: $PrivateKeyPath"
}

$encryptedKey = [IO.File]::ReadAllBytes($PrivateKeyPath)
$entropy = [Text.Encoding]::UTF8.GetBytes('MajesticBoost manifest signing key v1')
$privateKeyBytes = [Security.Cryptography.ProtectedData]::Unprotect(
    $encryptedKey,
    $entropy,
    [Security.Cryptography.DataProtectionScope]::CurrentUser)

$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
try {
    $privateKeyXml = [Text.Encoding]::UTF8.GetString($privateKeyBytes)
    $rsa.FromXmlString($privateKeyXml)
    if ($rsa.KeySize -lt 3072) {
        throw "Signing key is too small: $($rsa.KeySize) bits."
    }

    $manifestBytes = [IO.File]::ReadAllBytes($ManifestPath)
    if ($manifestBytes.Length -le 0 -or $manifestBytes.Length -gt 16384) {
        throw "Manifest size must be between 1 and 16384 bytes."
    }

    $sha256Oid = [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')
    $signature = $rsa.SignData($manifestBytes, $sha256Oid)
    $base64 = [Convert]::ToBase64String($signature)
    [IO.File]::WriteAllText($SignaturePath, $base64, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Signed $ManifestPath"
    Write-Host "Signature: $SignaturePath"
}
finally {
    if ($privateKeyBytes) {
        [Array]::Clear($privateKeyBytes, 0, $privateKeyBytes.Length)
    }
    if ($rsa) {
        $rsa.PersistKeyInCsp = $false
        $rsa.Clear()
    }
}
