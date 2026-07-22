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
    $InstallerPath = Join-Path $projectRoot 'dist\MajesticBoost-Setup-1.4.1.exe'
}

$testRoot = Join-Path $env:TEMP ('MajesticBoost-CompiledReplace-Test-' + [Guid]::NewGuid().ToString('N'))

function Test-CompiledHelper {
    param(
        [Parameter(Mandatory = $true)][string]$AssemblyPath,
        [Parameter(Mandatory = $true)][string]$TypeName,
        [Parameter(Mandatory = $true)][string]$FixtureName
    )
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($AssemblyPath))
    $type = $assembly.GetType($TypeName, $true, $false)
    $flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
    $method = $type.GetMethod('ReplaceFileWithoutRetainedBackup', $flags)
    if (-not $method) {
        throw "Compiled helper was not found: $TypeName"
    }

    $source = Join-Path $testRoot ($FixtureName + '-source.bin')
    $destination = Join-Path $testRoot ($FixtureName + '-destination.bin')
    [IO.File]::WriteAllText($source, 'new-value')
    [IO.File]::WriteAllText($destination, 'old-value')
    $invokeArguments = New-Object 'object[]' 2
    $invokeArguments[0] = [string]$source
    $invokeArguments[1] = [string]$destination
    [void]$method.Invoke($null, $invokeArguments)

    if ([IO.File]::ReadAllText($destination) -cne 'new-value') {
        throw "Compiled atomic replace produced the wrong destination: $TypeName"
    }
    if (Test-Path -LiteralPath $source) {
        throw "Compiled atomic replace did not consume its source: $TypeName"
    }
    $leftovers = @(Get-ChildItem -LiteralPath $testRoot -Filter ($FixtureName + '-destination.bin.replace-backup-*'))
    if ($leftovers.Count -ne 0) {
        throw "Compiled atomic replace left a backup behind: $TypeName"
    }
}

function Test-ApplicationCaller {
    param([Parameter(Mandatory = $true)][string]$AssemblyPath)
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($AssemblyPath))
    $type = $assembly.GetType('MajesticBoost.OptimizationFlowOverlay', $true, $false)
    $flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
    $method = $type.GetMethod('WriteUtf8Atomically', $flags)
    if (-not $method) { throw 'Compiled WriteUtf8Atomically caller was not found.' }

    $path = Join-Path $testRoot 'app-caller.txt'
    foreach ($value in @('first', 'second')) {
        $invokeArguments = New-Object 'object[]' 2
        $invokeArguments[0] = [string]$path
        $invokeArguments[1] = [string]$value
        [void]$method.Invoke($null, $invokeArguments)
    }
    if ([IO.File]::ReadAllText($path) -cne 'second') {
        throw 'Compiled application caller failed to replace an existing marker.'
    }
}

function Test-InstallerCallers {
    param([Parameter(Mandatory = $true)][string]$AssemblyPath)
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($AssemblyPath))
    $type = $assembly.GetType('MajesticBoostSetup.InstallerEngine', $true, $false)
    $flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
    $commit = $type.GetMethod('CommitStagedFile', $flags)
    $restore = $type.GetMethod('RestoreCommittedFile', $flags)
    if (-not $commit -or -not $restore) { throw 'Compiled installer transaction callers were not found.' }

    $stage = Join-Path $testRoot 'setup-stage.bin'
    $destination = Join-Path $testRoot 'setup-destination.bin'
    $backup = Join-Path $testRoot 'setup-backup.bin'
    [IO.File]::WriteAllText($stage, 'new-install')
    [IO.File]::WriteAllText($destination, 'old-install')

    $commitArguments = New-Object 'object[]' 4
    $commitArguments[0] = [string]$stage
    $commitArguments[1] = [string]$destination
    $commitArguments[2] = [string]$backup
    $commitArguments[3] = $true
    [void]$commit.Invoke($null, $commitArguments)
    if ([IO.File]::ReadAllText($destination) -cne 'new-install' -or
        [IO.File]::ReadAllText($backup) -cne 'old-install') {
        throw 'Compiled installer commit caller did not preserve the original file.'
    }

    $restoreArguments = New-Object 'object[]' 3
    $restoreArguments[0] = [string]$destination
    $restoreArguments[1] = [string]$backup
    $restoreArguments[2] = $true
    $restored = [bool]$restore.Invoke($null, $restoreArguments)
    if (-not $restored -or [IO.File]::ReadAllText($destination) -cne 'old-install') {
        throw 'Compiled installer rollback caller did not restore the original file.'
    }
}

try {
    [void](New-Item -ItemType Directory -Path $testRoot)
    Test-CompiledHelper -AssemblyPath $ApplicationPath -TypeName 'MajesticBoost.OptimizationFlowOverlay' -FixtureName 'app'
    Test-CompiledHelper -AssemblyPath $InstallerPath -TypeName 'MajesticBoostSetup.InstallerEngine' -FixtureName 'setup'
    Test-ApplicationCaller -AssemblyPath $ApplicationPath
    Test-InstallerCallers -AssemblyPath $InstallerPath
    Write-Host 'PASS: compiled application and installer atomic replace helpers work on .NET Framework.'
}
finally {
    $fullTestRoot = [IO.Path]::GetFullPath($testRoot)
    $fullTempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    if ($fullTestRoot.StartsWith($fullTempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $fullTestRoot) -like 'MajesticBoost-CompiledReplace-Test-*') {
        $testRootItem = Get-Item -LiteralPath $fullTestRoot -Force -ErrorAction SilentlyContinue
        if ($testRootItem -and ($testRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            Remove-Item -LiteralPath $fullTestRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
