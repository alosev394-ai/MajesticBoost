[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) {
    throw 'This regression test must run under Windows PowerShell 5.1.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $env:TEMP ('MajesticBoost-AtomicReplace-Test-' + [Guid]::NewGuid().ToString('N'))

function Get-ProductionFunctionScriptBlock {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $ScriptPath,
        [ref]$tokens,
        [ref]$errors)
    if ($errors.Count -ne 0) {
        throw "Cannot parse production script: $ScriptPath"
    }
    $definition = $ast.Find(
        {
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -ceq $Name
        },
        $true)
    if (-not $definition) {
        throw "Production function was not found: $Name"
    }
    return [ScriptBlock]::Create($definition.Extent.Text)
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Label)
    if ($Expected -cne $Actual) {
        throw "$Label expected '$Expected' but got '$Actual'."
    }
}

function Test-WriteAtomicFromScript {
    param([Parameter(Mandatory = $true)][string]$ScriptPath)
    & {
        param($ProductionScript, $FixtureRoot)
        $utf8NoBom = New-Object Text.UTF8Encoding($false)
        . (Get-ProductionFunctionScriptBlock -ScriptPath $ProductionScript -Name 'Replace-FileWithoutRetainedBackup')
        . (Get-ProductionFunctionScriptBlock -ScriptPath $ProductionScript -Name 'Write-TextAtomic')
        $firstValue = $utf8NoBom.GetString([Convert]::FromBase64String('0L/QtdGA0LLQsNGPINC30LDQv9C40YHRjA=='))
        $secondValue = $utf8NoBom.GetString([Convert]::FromBase64String('0L/QvtCy0YLQvtGA0L3QsNGPINC30LDQv9C40YHRjA=='))
        $iterationPrefix = $utf8NoBom.GetString([Convert]::FromBase64String('0LjRgtC10YDQsNGG0LjRjy0='))

        $destination = Join-Path $FixtureRoot ((Split-Path -Leaf $ProductionScript) + '.txt')
        Write-TextAtomic -Path $destination -Text $firstValue
        Assert-Equal -Expected $firstValue -Actual ([IO.File]::ReadAllText($destination)) -Label 'Create'

        Write-TextAtomic -Path $destination -Text $secondValue
        Assert-Equal -Expected $secondValue -Actual ([IO.File]::ReadAllText($destination)) -Label 'Replace'

        foreach ($index in 1..20) {
            $value = $iterationPrefix + $index
            Write-TextAtomic -Path $destination -Text $value
            Assert-Equal -Expected $value -Actual ([IO.File]::ReadAllText($destination)) -Label 'Repeated replace'
        }
    } $ScriptPath $testRoot
}

try {
    [void](New-Item -ItemType Directory -Path $testRoot)
    $applyScript = Join-Path $projectRoot 'outputs\MaxFPS-Apply.ps1'
    $restoreScript = Join-Path $projectRoot 'outputs\MaxFPS-Restore.ps1'

    Test-WriteAtomicFromScript -ScriptPath $applyScript
    Test-WriteAtomicFromScript -ScriptPath $restoreScript

    & {
        param($ProductionScript, $FixtureRoot)
        . (Get-ProductionFunctionScriptBlock -ScriptPath $ProductionScript -Name 'Replace-FileWithoutRetainedBackup')
        . (Get-ProductionFunctionScriptBlock -ScriptPath $ProductionScript -Name 'Get-FileSha256')
        . (Get-ProductionFunctionScriptBlock -ScriptPath $ProductionScript -Name 'Restore-FileAtomic')

        $backup = Join-Path $FixtureRoot 'backup.bin'
        $original = Join-Path $FixtureRoot 'original.bin'
        [IO.File]::WriteAllText($backup, 'before')
        [IO.File]::WriteAllText($original, 'after')
        $expectedHash = Get-FileSha256 -Path $backup
        Restore-FileAtomic -Backup $backup -Original $original -ExpectedHash $expectedHash
        Assert-Equal -Expected 'before' -Actual ([IO.File]::ReadAllText($original)) -Label 'Restore file'
        Assert-Equal -Expected 'before' -Actual ([IO.File]::ReadAllText($backup)) -Label 'Restore backup preservation'

        [IO.File]::WriteAllText($original, 'user-value')
        $failedAsExpected = $false
        try {
            Restore-FileAtomic -Backup $backup -Original $original -ExpectedHash ('0' * 64)
        }
        catch {
            $failedAsExpected = $true
        }
        if (-not $failedAsExpected) {
            throw 'Restore-FileAtomic accepted an invalid expected hash.'
        }
        Assert-Equal -Expected 'user-value' -Actual ([IO.File]::ReadAllText($original)) -Label 'Hash mismatch preservation'
    } $restoreScript $testRoot

    $leftovers = @(Get-ChildItem -LiteralPath $testRoot -Force | Where-Object {
        $_.Name -like '*.tmp' -or $_.Name -like '*.replace-backup'
    })
    if ($leftovers.Count -ne 0) {
        throw 'Atomic replacement left temporary files behind.'
    }

    Write-Host 'PASS: production atomic replace functions are compatible with Windows PowerShell 5.1.'
}
finally {
    $fullTestRoot = [IO.Path]::GetFullPath($testRoot)
    $fullTempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    if ($fullTestRoot.StartsWith($fullTempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $fullTestRoot) -like 'MajesticBoost-AtomicReplace-Test-*') {
        $testRootItem = Get-Item -LiteralPath $fullTestRoot -Force -ErrorAction SilentlyContinue
        if ($testRootItem -and ($testRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            Remove-Item -LiteralPath $fullTestRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
