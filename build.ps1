[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpfRoot = Join-Path $frameworkRoot 'WPF'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$workDirectory = Join-Path $projectRoot 'work'
$distDirectory = Join-Path $projectRoot 'dist'
$releaseVersion = '1.6.4'
$appOutput = Join-Path $workDirectory 'MajesticBoost.exe'
$setupOutput = Join-Path $workDirectory "MajesticBoost-Setup-$releaseVersion.exe"
$versionedSetupOutput = Join-Path $distDirectory "MajesticBoost-Setup-$releaseVersion.exe"
$latestSetupOutput = Join-Path $distDirectory 'MajesticBoost-Setup-Latest.exe'
$presentMonPath = Join-Path $projectRoot 'third_party\PresentMon\PresentMon.exe'
$presentMonLicensePath = Join-Path $projectRoot 'third_party\PresentMon\LICENSE.txt'
$presentMonThirdPartyPath = Join-Path $projectRoot 'third_party\PresentMon\THIRD_PARTY.txt'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw ".NET Framework C# compiler not found: $compiler"
}

$presentMon = Get-Item -LiteralPath $presentMonPath -ErrorAction Stop
$presentMonHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $presentMonPath).Hash.ToLowerInvariant()
if ($presentMon.Length -ne 956768 -or
    $presentMonHash -cne '9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191') {
    throw 'Pinned PresentMon 2.5.1 binary failed its size or SHA-256 validation.'
}
foreach ($licensePath in @($presentMonLicensePath, $presentMonThirdPartyPath)) {
    if (-not (Test-Path -LiteralPath $licensePath) -or (Get-Item -LiteralPath $licensePath).Length -eq 0) {
        throw "PresentMon notice is missing or empty: $licensePath"
    }
}

[void](New-Item -ItemType Directory -Path $workDirectory -Force)
[void](New-Item -ItemType Directory -Path $distDirectory -Force)

$appArguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    "/win32icon:$projectRoot\MajesticBoost\MajesticBoost.ico",
    "/win32manifest:$projectRoot\MajesticBoost\app.manifest",
    '/reference:System.dll',
    '/reference:System.Core.dll',
    "/reference:$frameworkRoot\System.Xaml.dll",
    "/reference:$wpfRoot\WindowsBase.dll",
    "/reference:$wpfRoot\PresentationCore.dll",
    "/reference:$wpfRoot\PresentationFramework.dll",
    "/out:$appOutput",
    "$projectRoot\MajesticBoost\Program.cs",
    "$projectRoot\MajesticBoost\BoostFeatures.cs",
    "$projectRoot\MajesticBoost\BoostCenterOverlay.cs",
    "$projectRoot\MajesticBoost\PerformanceCapture.cs",
    "$projectRoot\MajesticBoost\OptimizationFlow.cs",
    "$projectRoot\MajesticBoost\UpdateFlow.cs"
)

& $compiler @appArguments
if ($LASTEXITCODE -ne 0) {
    throw "Majestic Boost compilation failed with exit code $LASTEXITCODE."
}

$setupArguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    "/win32icon:$projectRoot\MajesticBoost\MajesticBoost.ico",
    "/win32manifest:$projectRoot\MajesticBoostInstaller\app.manifest",
    '/reference:System.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    "/resource:$appOutput,MajesticBoost.Payload.exe",
    "/resource:$projectRoot\outputs\Game-Boost.ps1,MajesticBoost.GameBoost.ps1",
    "/resource:$projectRoot\outputs\MaxFPS-Apply.ps1,MajesticBoost.MaxFPSApply.ps1",
    "/resource:$projectRoot\outputs\MaxFPS-Restore.ps1,MajesticBoost.MaxFPSRestore.ps1",
    "/resource:$presentMonPath,MajesticBoost.PresentMon.exe",
    "/resource:$presentMonLicensePath,MajesticBoost.PresentMon.License.txt",
    "/resource:$presentMonThirdPartyPath,MajesticBoost.PresentMon.ThirdParty.txt",
    "/out:$setupOutput",
    "$projectRoot\MajesticBoostInstaller\Program.cs"
)

& $compiler @setupArguments
if ($LASTEXITCODE -ne 0) {
    throw "Majestic Boost installer compilation failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath $appOutput -Destination (Join-Path $distDirectory 'MajesticBoost.exe') -Force
Copy-Item -LiteralPath $setupOutput -Destination $versionedSetupOutput -Force
Copy-Item -LiteralPath $setupOutput -Destination $latestSetupOutput -Force

$releaseFiles = @(
    (Join-Path $distDirectory 'MajesticBoost.exe'),
    $versionedSetupOutput,
    $latestSetupOutput
)
$hashes = Get-FileHash -Algorithm SHA256 -LiteralPath $releaseFiles
$hashLines = foreach ($hash in $hashes) {
    $hash.Hash + ' *' + (Split-Path -Leaf $hash.Path)
}
[IO.File]::WriteAllLines(
    (Join-Path $distDirectory 'SHA256SUMS.txt'),
    $hashLines,
    (New-Object Text.UTF8Encoding($false)))
$hashes | Format-Table -AutoSize
