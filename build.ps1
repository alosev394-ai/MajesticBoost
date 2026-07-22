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
$appOutput = Join-Path $workDirectory 'MajesticBoost.exe'
$setupOutput = Join-Path $workDirectory 'MajesticBoost-Setup-1.5.0.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw ".NET Framework C# compiler not found: $compiler"
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
    "/out:$setupOutput",
    "$projectRoot\MajesticBoostInstaller\Program.cs"
)

& $compiler @setupArguments
if ($LASTEXITCODE -ne 0) {
    throw "Majestic Boost installer compilation failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath $appOutput -Destination (Join-Path $distDirectory 'MajesticBoost.exe') -Force
Copy-Item -LiteralPath $setupOutput -Destination (Join-Path $distDirectory 'MajesticBoost-Setup-1.5.0.exe') -Force

Get-FileHash -Algorithm SHA256 -LiteralPath @(
    (Join-Path $distDirectory 'MajesticBoost.exe'),
    (Join-Path $distDirectory 'MajesticBoost-Setup-1.5.0.exe')
) | Format-Table -AutoSize
