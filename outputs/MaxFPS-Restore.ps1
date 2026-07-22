[CmdletBinding()]
param(
    [string]$StatePath,
    [string]$ResultPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$utf8NoBom = New-Object Text.UTF8Encoding($false)
$systemDirectory = [IO.Path]::GetFullPath([Environment]::SystemDirectory)
$programDataRoot = [IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData))
$programFilesRoot = [IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles))
if (-not $systemDirectory -or -not $programDataRoot -or -not $programFilesRoot) { throw 'Trusted Windows security roots could not be resolved from the OS.' }
$stateRoot = Join-Path $programDataRoot 'CodexGamingOptimization'
$backupRoot = Join-Path $stateRoot 'Backups'
$latestStatePointer = Join-Path $stateRoot 'latest-state.txt'
$transactionLockPath = Join-Path $stateRoot 'transaction.lock'
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentUserSid = $currentIdentity.User
$administratorsSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
$systemSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
$storageHadUntrustedWriteBeforeProtection = $false
$untrustedPointerWasDiscarded = $false

function Get-SafeResultPath {
    param([string]$RequestedPath)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $candidate = if ($RequestedPath) { [IO.Path]::GetFullPath($RequestedPath) } else {
        Join-Path $tempRoot ('MajesticBoost-restore-{0}.json' -f [Guid]::NewGuid().ToString('N'))
    }
    if ([IO.Path]::GetDirectoryName($candidate) -ine $tempRoot) {
        throw 'ResultPath must be a direct child of the current user TEMP directory.'
    }
    if ([IO.Path]::GetFileName($candidate) -notmatch '^MajesticBoost-restore-[0-9a-fA-F]{32}\.json$') {
        throw 'ResultPath must use the MajesticBoost restore GUID filename format.'
    }
    if (Test-Path -LiteralPath $candidate) { throw 'ResultPath must be a new, non-existing unique file.' }
    $tempItem = Get-Item -LiteralPath $tempRoot -Force
    if (($tempItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The current user TEMP directory must not be a reparse point.'
    }
    return $candidate
}

$effectiveResultPath = Get-SafeResultPath -RequestedPath $ResultPath

if ($StatePath) {
    if ($StatePath.IndexOfAny([char[]]@('"', "`r", "`n", [char]0)) -ge 0) { throw 'StatePath contains invalid characters.' }
    $earlyStatePath = [IO.Path]::GetFullPath($StatePath)
    $earlyBackupRoot = [IO.Path]::GetFullPath($backupRoot).TrimEnd('\')
    $earlyTransaction = [IO.Path]::GetDirectoryName($earlyStatePath)
    if ([IO.Path]::GetFileName($earlyStatePath) -ine 'state.json' -or
        [IO.Path]::GetDirectoryName($earlyTransaction) -ine $earlyBackupRoot) {
        throw 'StatePath must identify a direct transaction state.json under the protected backup root.'
    }
    $StatePath = $earlyStatePath
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Quote-ProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    return '"{0}"' -f $Value.Replace('"', '\"')
}

if (-not (Test-IsAdministrator)) {
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Quote-ProcessArgument -Value $PSCommandPath)
    )
    if ($StatePath) {
        $arguments += @('-StatePath', (Quote-ProcessArgument -Value $StatePath))
    }
    $arguments += @('-ResultPath', (Quote-ProcessArgument -Value $effectiveResultPath))
    $trustedPowerShell = Join-Path $systemDirectory 'WindowsPowerShell\v1.0\powershell.exe'
    $process = Start-Process -FilePath $trustedPowerShell -Verb RunAs -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    exit $process.ExitCode
}

$systemModuleRoot = Join-Path $systemDirectory 'WindowsPowerShell\v1.0\Modules'
$env:PSModulePath = $systemModuleRoot
foreach ($moduleManifest in @(
    (Join-Path $systemModuleRoot 'CimCmdlets\CimCmdlets.psd1'),
    (Join-Path $systemModuleRoot 'ScheduledTasks\ScheduledTasks.psd1'),
    (Join-Path $systemModuleRoot 'Defender\Defender.psd1')
)) {
    if (-not (Test-Path -LiteralPath $moduleManifest -PathType Leaf)) {
        throw "Required protected Windows module is missing: $moduleManifest"
    }
    Microsoft.PowerShell.Core\Import-Module -Name $moduleManifest -Force -ErrorAction Stop
}

function Set-ObjectProperty {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        $Value
    )
    if ($Object.PSObject.Properties.Name -contains $Name) {
        $Object.$Name = $Value
    }
    else {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
}

function Replace-FileWithoutRetainedBackup {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    $discardBackup = '{0}.{1}.replace-backup' -f $Destination, [Guid]::NewGuid().ToString('N')
    try {
        # Windows PowerShell 5.1 coerces $null to an empty string for this
        # overload, which makes File.Replace fail with "path is not legal".
        # A real unique backup path keeps the replace atomic on .NET Framework.
        [IO.File]::Replace($Source, $Destination, $discardBackup, $true)
    }
    finally {
        Remove-Item -LiteralPath $discardBackup -Force -ErrorAction SilentlyContinue
    }
}

function Write-TextAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text
    )
    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $temporaryPath = '{0}.{1}.tmp' -f $Path, [Guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($temporaryPath, $Text, $utf8NoBom)
        if (Test-Path -LiteralPath $Path) {
            Replace-FileWithoutRetainedBackup -Source $temporaryPath -Destination $Path
        }
        else {
            [IO.File]::Move($temporaryPath, $Path)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Write-JsonAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value,
        [int]$Depth = 18
    )
    Write-TextAtomic -Path $Path -Text ($Value | ConvertTo-Json -Depth $Depth)
}

function Write-ResultJson {
    param([Parameter(Mandatory = $true)]$Value, [int]$Depth = 10)
    $bytes = $utf8NoBom.GetBytes(($Value | ConvertTo-Json -Depth $Depth))
    $stream = $null
    $created = $false
    try {
        $stream = New-Object IO.FileStream($effectiveResultPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
        $created = $true
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    catch {
        if ($stream) { $stream.Dispose(); $stream = $null }
        if ($created) { Remove-Item -LiteralPath $effectiveResultPath -Force -ErrorAction SilentlyContinue }
        throw
    }
    finally { if ($stream) { $stream.Dispose() } }
}

function Assert-NoReparsePath {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$StopAt)
    $candidate = [IO.Path]::GetFullPath($Path)
    $stop = [IO.Path]::GetFullPath($StopAt).TrimEnd('\')
    if (-not ($candidate -ieq $stop) -and -not $candidate.StartsWith($stop + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside its trusted root: $candidate"
    }
    $cursor = $candidate
    while ($true) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse points are not allowed in protected paths: $cursor"
            }
        }
        if ($cursor -ieq $stop) { break }
        $parent = Split-Path -Parent $cursor
        if (-not $parent -or $parent -eq $cursor) { throw "Unable to reach trusted root from: $candidate" }
        $cursor = $parent
    }
}

function Test-TrustedOwner {
    param([Parameter(Mandatory = $true)][Security.AccessControl.FileSystemSecurity]$Security)
    $owner = $Security.GetOwner([Security.Principal.SecurityIdentifier])
    return $owner -eq $administratorsSid -or $owner -eq $systemSid
}

function Test-HasUntrustedWriteAce {
    param([Parameter(Mandatory = $true)][Security.AccessControl.FileSystemSecurity]$Security)
    # Use only atomic mutation bits. Composite rights such as Write, Modify and
    # FullControl also contain read bits and would misclassify ReadAndExecute.
    $dangerous = [int][Security.AccessControl.FileSystemRights]::WriteData -bor
        [int][Security.AccessControl.FileSystemRights]::AppendData -bor
        [int][Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [int][Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [int][Security.AccessControl.FileSystemRights]::Delete -bor
        [int][Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [int][Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [int][Security.AccessControl.FileSystemRights]::TakeOwnership
    # Preserve raw generic ACE protection if Windows has not mapped it to
    # object-specific rights yet: GENERIC_ALL | GENERIC_WRITE.
    $genericMutationRights = 0x10000000 -bor 0x40000000
    foreach ($rule in $Security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
        if ($rule.IdentityReference -eq $administratorsSid -or $rule.IdentityReference -eq $systemSid) { continue }
        $ruleRights = [int]$rule.FileSystemRights
        if (($ruleRights -band $dangerous) -ne 0 -or ($ruleRights -band $genericMutationRights) -ne 0) { return $true }
    }
    return $false
}

function New-ProtectedFileSystemSecurity {
    param([switch]$Directory)
    $security = if ($Directory) { New-Object Security.AccessControl.DirectorySecurity } else { New-Object Security.AccessControl.FileSecurity }
    $security.SetOwner($administratorsSid)
    $security.SetAccessRuleProtection($true, $false)
    if ($Directory) {
        $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
        $propagation = [Security.AccessControl.PropagationFlags]::None
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($systemSid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, [Security.AccessControl.AccessControlType]::Allow)))
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($administratorsSid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, [Security.AccessControl.AccessControlType]::Allow)))
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($currentUserSid, [Security.AccessControl.FileSystemRights]::ReadAndExecute, $inheritance, $propagation, [Security.AccessControl.AccessControlType]::Allow)))
    }
    else {
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($systemSid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow)))
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($administratorsSid, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow)))
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($currentUserSid, [Security.AccessControl.FileSystemRights]::ReadAndExecute, [Security.AccessControl.AccessControlType]::Allow)))
    }
    return $security
}

function Set-ProtectedDirectorySecurity {
    param([Parameter(Mandatory = $true)][string]$Path)
    $wasExisting = Test-Path -LiteralPath $Path
    if (-not $wasExisting) { [void][IO.Directory]::CreateDirectory($Path) }
    Assert-NoReparsePath -Path $Path -StopAt $programDataRoot
    $existing = [IO.Directory]::GetAccessControl($Path)
    if ($wasExisting -and -not (Test-TrustedOwner -Security $existing)) { throw "Protected state directory has an untrusted owner: $Path" }
    [IO.Directory]::SetAccessControl($Path, (New-ProtectedFileSystemSecurity -Directory))
    $verified = [IO.Directory]::GetAccessControl($Path)
    if (-not $verified.AreAccessRulesProtected -or -not (Test-TrustedOwner -Security $verified) -or (Test-HasUntrustedWriteAce -Security $verified)) {
        throw "Unable to enforce protected DACL on state directory: $Path"
    }
}

function Set-ProtectedFileSecurity {
    param([Parameter(Mandatory = $true)][string]$Path, [switch]$RequireTrustedExisting)
    Assert-NoReparsePath -Path $Path -StopAt $programDataRoot
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Protected state file is missing: $Path" }
    $existing = [IO.File]::GetAccessControl($Path)
    if ($RequireTrustedExisting -and ((-not (Test-TrustedOwner -Security $existing)) -or (Test-HasUntrustedWriteAce -Security $existing))) {
        throw "Existing protected state file is user-owned or user-writable: $Path"
    }
    [IO.File]::SetAccessControl($Path, (New-ProtectedFileSystemSecurity))
    $verified = [IO.File]::GetAccessControl($Path)
    if (-not $verified.AreAccessRulesProtected -or -not (Test-TrustedOwner -Security $verified) -or (Test-HasUntrustedWriteAce -Security $verified)) {
        throw "Unable to enforce protected DACL on state file: $Path"
    }
}

function Assert-ProtectedExistingFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    Assert-NoReparsePath -Path $Path -StopAt $programDataRoot
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Protected file is missing: $Path" }
    $security = [IO.File]::GetAccessControl($Path)
    if (-not $security.AreAccessRulesProtected -or -not (Test-TrustedOwner -Security $security) -or (Test-HasUntrustedWriteAce -Security $security)) {
        throw "Protected file owner/DACL validation failed: $Path"
    }
}

function Write-ProtectedTextAtomic {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text)
    Write-TextAtomic -Path $Path -Text $Text
    Set-ProtectedFileSecurity -Path $Path
}

function Write-ProtectedJsonAtomic {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)]$Value, [int]$Depth = 18)
    Write-ProtectedTextAtomic -Path $Path -Text ($Value | ConvertTo-Json -Depth $Depth)
}

function Initialize-ProtectedStateStorage {
    if (-not (Test-Path -LiteralPath $stateRoot -PathType Container) -or -not (Test-Path -LiteralPath $backupRoot -PathType Container)) {
        throw 'Protected MAX FPS state storage does not exist.'
    }
    foreach ($existingDirectory in @($stateRoot, $backupRoot)) {
        Assert-NoReparsePath -Path $existingDirectory -StopAt $programDataRoot
        $preSecurity = [IO.Directory]::GetAccessControl($existingDirectory)
        if (-not (Test-TrustedOwner -Security $preSecurity)) { throw "Existing state storage has an untrusted owner: $existingDirectory" }
        if (Test-HasUntrustedWriteAce -Security $preSecurity) { $script:storageHadUntrustedWriteBeforeProtection = $true }
    }
    if (-not $storageHadUntrustedWriteBeforeProtection) {
        foreach ($existingControlFile in @($latestStatePointer, $transactionLockPath)) {
            if (-not (Test-Path -LiteralPath $existingControlFile -PathType Leaf)) { continue }
            Assert-NoReparsePath -Path $existingControlFile -StopAt $programDataRoot
            $preSecurity = [IO.File]::GetAccessControl($existingControlFile)
            if (-not (Test-TrustedOwner -Security $preSecurity) -or (Test-HasUntrustedWriteAce -Security $preSecurity)) {
                throw "Existing state control file is not trustworthy: $existingControlFile"
            }
        }
    }
    Set-ProtectedDirectorySecurity -Path $stateRoot
    Set-ProtectedDirectorySecurity -Path $backupRoot
    if ($storageHadUntrustedWriteBeforeProtection) {
        foreach ($untrustedControlPath in @($latestStatePointer, $transactionLockPath)) {
            $untrustedControlItem = Get-Item -LiteralPath $untrustedControlPath -Force -ErrorAction SilentlyContinue
            if (-not $untrustedControlItem) { continue }
            $quarantineName = '{0}.quarantined-untrusted-{1}' -f $untrustedControlPath, [Guid]::NewGuid().ToString('N')
            Move-Item -LiteralPath $untrustedControlPath -Destination $quarantineName -Force
            if ($untrustedControlPath -ieq $latestStatePointer) { $script:untrustedPointerWasDiscarded = $true }
        }
        $newLock = [IO.File]::Open($transactionLockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $newLock.Dispose()
        Set-ProtectedFileSecurity -Path $transactionLockPath
        return
    }
    if (-not (Test-Path -LiteralPath $latestStatePointer -PathType Leaf)) { throw 'No active Majestic Boost MAX FPS transaction was found.' }
    Set-ProtectedFileSecurity -Path $latestStatePointer -RequireTrustedExisting
    if ((Get-Item -LiteralPath $latestStatePointer -Force).Length -gt 4096) { throw 'The MAX FPS state pointer exceeds the safe size limit.' }
    if (Test-Path -LiteralPath $transactionLockPath) {
        Set-ProtectedFileSecurity -Path $transactionLockPath -RequireTrustedExisting
    }
    else {
        $newLock = [IO.File]::Open($transactionLockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $newLock.Dispose()
        Set-ProtectedFileSecurity -Path $transactionLockPath
    }
}

function Test-StatePathAllowed {
    param([Parameter(Mandatory = $true)][string]$Path)
    $candidate = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($backupRoot).TrimEnd('\') + '\'
    if (-not $candidate.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($candidate) -ine 'state.json') { return $false }
    $transactionDirectory = [IO.Path]::GetDirectoryName($candidate)
    return [IO.Path]::GetDirectoryName($transactionDirectory) -ieq [IO.Path]::GetFullPath($backupRoot).TrimEnd('\')
}

function Test-StateProvenanceTrusted {
    param([Parameter(Mandatory = $true)][string]$Path)
    try {
        if (-not (Test-StatePathAllowed -Path $Path)) { return $false }
        $candidate = [IO.Path]::GetFullPath($Path)
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $false }
        Assert-NoReparsePath -Path $candidate -StopAt $programDataRoot
        $directorySecurity = [IO.Directory]::GetAccessControl((Split-Path -Parent $candidate))
        $fileSecurity = [IO.File]::GetAccessControl($candidate)
        return $directorySecurity.AreAccessRulesProtected -and $fileSecurity.AreAccessRulesProtected -and
            (Test-TrustedOwner -Security $directorySecurity) -and (Test-TrustedOwner -Security $fileSecurity) -and
            -not (Test-HasUntrustedWriteAce -Security $directorySecurity) -and -not (Test-HasUntrustedWriteAce -Security $fileSecurity)
    }
    catch { return $false }
}

function Get-ValidatedStatePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $candidate = [IO.Path]::GetFullPath($Path)
    if (-not (Test-StatePathAllowed -Path $candidate)) {
        throw "Optimization state path is outside the protected backup root: $candidate"
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Optimization state does not exist: $candidate"
    }
    $stateDirectory = Split-Path -Parent $candidate
    Set-ProtectedDirectorySecurity -Path $stateDirectory
    Set-ProtectedFileSecurity -Path $candidate -RequireTrustedExisting
    if ((Get-Item -LiteralPath $candidate -Force).Length -gt 2097152) { throw 'Optimization state exceeds the safe size limit.' }
    return $candidate
}

function Test-BackupFilePathAllowed {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$StateDirectory
    )
    $candidate = [IO.Path]::GetFullPath($Path)
    $directory = [IO.Path]::GetFullPath($StateDirectory).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($candidate) -ine $directory) { return $false }
    $leaf = [IO.Path]::GetFileName($candidate)
    return $leaf -match '^[0-9a-fA-F]{32}-(settings\.xml|majestic\.json)$'
}

function Enter-TransactionLock {
    try {
        $stream = [IO.File]::Open(
            $transactionLockPath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
        return $stream
    }
    catch [IO.IOException] {
        throw 'Another MAX FPS apply/restore transaction is already running.'
    }
}

function Invoke-PowerCfg {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $powerCfg = Join-Path $systemDirectory 'powercfg.exe'
    $output = & $powerCfg @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | Out-String).Trim()
    if ($exitCode -ne 0) {
        throw "powercfg $($Arguments -join ' ') failed with exit code $exitCode. $text"
    }
    return $text
}

function Get-ActivePowerScheme {
    $output = Invoke-PowerCfg -Arguments @('/getactivescheme')
    $match = [regex]::Match($output, '[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}')
    if (-not $match.Success) { throw "Unable to parse the active power scheme GUID: $output" }
    return $match.Value.ToLowerInvariant()
}

function Test-PowerSchemeExists {
    param([Parameter(Mandatory = $true)][string]$Guid)
    return (Invoke-PowerCfg -Arguments @('/list')) -match [regex]::Escape($Guid)
}

function ConvertTo-ComparableJson {
    param($Value)
    if ($null -eq $Value) { return '<null>' }
    return ($Value | ConvertTo-Json -Compress -Depth 12)
}

function Test-ValueEqual {
    param($Left, $Right)
    return (ConvertTo-ComparableJson -Value $Left) -ceq (ConvertTo-ComparableJson -Value $Right)
}

function Test-GuidText {
    param([string]$Value)
    $parsed = [Guid]::Empty
    return [Guid]::TryParseExact($Value, 'D', [ref]$parsed)
}

function Get-AllowedRegistryTarget {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Name)
    $key = ('{0}|{1}' -f $Path.TrimEnd('\'), $Name).ToLowerInvariant()
    $dwordTargets = @{
        'hkcu:\software\microsoft\gamebar|autogamemodeenabled' = 1
        'hkcu:\software\microsoft\gamebar|allowautogamemode' = 1
        'hkcu:\software\microsoft\windows\currentversion\gamedvr|appcaptureenabled' = 0
        'hkcu:\software\microsoft\windows\currentversion\gamedvr|historicalcaptureenabled' = 0
        'hkcu:\system\gameconfigstore|gamedvr_enabled' = 0
        'hklm:\system\currentcontrolset\control\graphicsdrivers|hwschmode' = 2
        'hklm:\system\currentcontrolset\control\power\powerthrottling|powerthrottlingoff' = 1
        'hklm:\software\microsoft\windows nt\currentversion\multimedia\systemprofile|systemresponsiveness' = 10
        'hkcu:\software\microsoft\windows\currentversion\explorer\visualeffects|visualfxsetting' = 2
        'hkcu:\software\microsoft\windows\currentversion\themes\personalize|enabletransparency' = 0
        'hklm:\software\policies\microsoft\dsh|allownewsandinterests' = 0
        'hkcu:\software\microsoft\windows\currentversion\dsh|isprelaunchenabled' = 0
    }
    if ($dwordTargets.ContainsKey($key)) {
        return [pscustomobject]@{ KeyExists = $true; Exists = $true; Kind = 'DWord'; Value = [int]$dwordTargets[$key] }
    }
    $shadowPrefix = 'hkcu:\software\nvidia corporation\global\shadowplay\nvspcaps|'
    $shadowNames = @('recenabled','dwmenabled','dwmdvrenabledv1','enablemicrophone','isshadowplayenabled','isshadowplayenableduser','hlenabled','dwmenableduser')
    if ($key.StartsWith($shadowPrefix, [StringComparison]::OrdinalIgnoreCase) -and $shadowNames -contains $Name.ToLowerInvariant()) {
        return [pscustomobject]@{ KeyExists = $true; Exists = $true; Kind = 'Binary'; Value = [byte[]](0, 0, 0, 0) }
    }
    if ($Path.TrimEnd('\') -ieq 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences') {
        try {
            if (-not [IO.Path]::IsPathRooted($Name)) { return $null }
            $canonical = [IO.Path]::GetFullPath($Name)
            if ($canonical -ine $Name -or @('GTA5.exe','GTA5_Enhanced.exe','GTA5_BE.exe','PlayGTAV.exe','Majestic Launcher.exe') -notcontains [IO.Path]::GetFileName($canonical)) { return $null }
            return [pscustomobject]@{ KeyExists = $true; Exists = $true; Kind = 'String'; Value = 'GpuPreference=2;' }
        }
        catch { return $null }
    }
    return $null
}

function Test-AllowedManagedFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    try {
        $candidate = [IO.Path]::GetFullPath($Path)
        $allowed = @(
            (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Rockstar Games\GTA V\settings.xml'),
            (Join-Path $env:APPDATA 'majestic-launcher\Multiplayer\majestic.json')
        ) | ForEach-Object { [IO.Path]::GetFullPath($_) }
        return @($allowed | Where-Object { $_ -ieq $candidate }).Count -eq 1
    }
    catch { return $false }
}

function Assert-RegistrySnapshotShape {
    param([Parameter(Mandatory = $true)]$Snapshot, [Parameter(Mandatory = $true)][string]$Label)
    foreach ($property in @('KeyExists','Exists','Kind','Value')) {
        if (-not ($Snapshot.PSObject.Properties.Name -contains $property)) { throw "$Label is missing $property." }
    }
    if ([bool]$Snapshot.Exists -and @('DWord','QWord','String','ExpandString','MultiString','Binary') -notcontains [string]$Snapshot.Kind) {
        throw "$Label has an unsupported registry kind."
    }
}

function Assert-MutationStateSchema {
    param(
        [Parameter(Mandatory = $true)]$Candidate,
        [Parameter(Mandatory = $true)][string]$ExpectedStatePath,
        [Parameter(Mandatory = $true)][string]$StateDirectory
    )
    if (-not ($Candidate.PSObject.Properties.Name -contains 'Version') -or [int]$Candidate.Version -ne 2) { throw 'Only state schema Version 2 can be restored.' }
    if (-not (Test-GuidText -Value ([string]$Candidate.OperationId))) { throw 'State OperationId is not a canonical GUID.' }
    if ([IO.Path]::GetFullPath([string]$Candidate.StateFile) -ine $ExpectedStatePath) { throw 'State self-reference does not match StatePath.' }
    if ([IO.Path]::GetFullPath([string]$Candidate.BackupDirectory) -ine $StateDirectory) { throw 'State BackupDirectory does not match its transaction directory.' }
    if ([string]$Candidate.Status -notin @('Applying','Active','ApplyFailed','AbortedNoChanges','RestoreIncomplete','Restoring','Restored','RestoredWithConflicts')) {
        throw "State status is not restorable: $($Candidate.Status)"
    }
    foreach ($collectionName in @('Registry','Services','Tasks','Files','MovedDirectories')) {
        if (-not ($Candidate.PSObject.Properties.Name -contains $collectionName)) { throw "State is missing $collectionName." }
    }

    $mutationsStarted = if ($Candidate.PSObject.Properties.Name -contains 'MutationsStarted') {
        if ($Candidate.MutationsStarted -isnot [bool]) { throw 'State MutationsStarted must be Boolean.' }
        [bool]$Candidate.MutationsStarted
    }
    else { $true }
    $phase = if ($Candidate.PSObject.Properties.Name -contains 'Phase') { [string]$Candidate.Phase } else { 'LegacyV2' }
    $trackedNonPowerMutation = @($Candidate.Registry | Where-Object { $_.ChangedByUs }).Count -gt 0 -or
        @($Candidate.Services | Where-Object { $_.ChangedByUs }).Count -gt 0 -or
        @($Candidate.Tasks | Where-Object { $_.ChangedByUs }).Count -gt 0 -or
        @($Candidate.Files | Where-Object { $_.ChangedByUs }).Count -gt 0 -or
        @($Candidate.MovedDirectories | Where-Object { $_.ChangedByUs }).Count -gt 0
    if (($Candidate.PSObject.Properties.Name -contains 'Defender') -and $Candidate.Defender) {
        $trackedNonPowerMutation = $trackedNonPowerMutation -or @($Candidate.Defender.Settings | Where-Object { $_.ChangedByUs }).Count -gt 0
    }
    $trackedPowerMutation = ($Candidate.PSObject.Properties.Name -contains 'Power') -and $Candidate.Power -and
        ($Candidate.Power.PSObject.Properties.Name -contains 'ChangedByUs') -and [bool]$Candidate.Power.ChangedByUs

    if (-not $mutationsStarted) {
        if ($phase -notin @('Prepared','PowerPlanCreationFailedNoChanges','AbortedNoChanges')) { throw "No-change state has an invalid phase: $phase" }
        if ($trackedNonPowerMutation -or $trackedPowerMutation) { throw 'No-change state contains tracked system mutations.' }
        return
    }

    if ($phase -in @('PowerPlanCreationPending','PowerPlanCreationAmbiguous')) {
        if (-not (Test-GuidText -Value ([string]$Candidate.OriginalPowerScheme)) -or $trackedNonPowerMutation -or $trackedPowerMutation) {
            throw 'Pending power-plan state contains invalid or unexpected mutation tracking.'
        }
        if (-not ($Candidate.PSObject.Properties.Name -contains 'PowerSchemesBefore')) { throw 'Pending power-plan state lacks its before-list.' }
        foreach ($guid in @($Candidate.PowerSchemesBefore)) { if (-not (Test-GuidText -Value ([string]$guid))) { throw 'Pending power-plan before-list contains an invalid GUID.' } }
        if ($phase -eq 'PowerPlanCreationAmbiguous') {
            if (-not ($Candidate.PSObject.Properties.Name -contains 'UnknownCreatedPowerSchemes') -or @($Candidate.UnknownCreatedPowerSchemes).Count -eq 0) { throw 'Ambiguous power-plan state lacks candidate GUIDs.' }
            foreach ($guid in @($Candidate.UnknownCreatedPowerSchemes)) { if (-not (Test-GuidText -Value ([string]$guid))) { throw 'Ambiguous power-plan state contains an invalid GUID.' } }
        }
        return
    }

    if ($phase -notin @('LegacyV2','PowerPlanCreated','ApplyingSettings','Active')) {
        throw "Mutation state has an invalid phase: $phase"
    }

    if (-not (Test-GuidText -Value ([string]$Candidate.OriginalPowerScheme)) -or -not (Test-GuidText -Value ([string]$Candidate.MaxPowerScheme))) {
        throw 'State power scheme GUIDs are invalid.'
    }
    if ([string]$Candidate.OriginalPowerScheme -ieq [string]$Candidate.MaxPowerScheme) { throw 'Original and MAX FPS power scheme GUIDs must differ.' }

    $seenRegistry = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($Candidate.Registry)) {
        if (-not [bool]$entry.ChangedByUs) { continue }
        $identity = '{0}|{1}' -f [string]$entry.Path, [string]$entry.Name
        if (-not $seenRegistry.Add($identity)) { throw "Duplicate registry mutation in state: $identity" }
        $allowed = Get-AllowedRegistryTarget -Path ([string]$entry.Path) -Name ([string]$entry.Name)
        if ($null -eq $allowed) { throw "Registry mutation is not allowlisted: $identity" }
        Assert-RegistrySnapshotShape -Snapshot $entry.Before -Label "Registry Before $identity"
        Assert-RegistrySnapshotShape -Snapshot $entry.Target -Label "Registry Target $identity"
        if (-not (Test-RegistrySnapshotEqual -Left $entry.Target -Right $allowed)) { throw "Registry target does not match the allowlist: $identity" }
    }

    $seenFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($Candidate.Files)) {
        if (-not [bool]$file.ChangedByUs) { continue }
        $original = [IO.Path]::GetFullPath([string]$file.Original)
        if (-not $seenFiles.Add($original)) { throw "Duplicate file mutation in state: $original" }
        if (-not (Test-AllowedManagedFile -Path $original)) { throw "File restore target is not allowlisted: $original" }
        Assert-NoReparsePath -Path $original -StopAt ([Environment]::GetFolderPath('UserProfile'))
        if ((Test-Path -LiteralPath $original) -and -not (Test-Path -LiteralPath $original -PathType Leaf)) { throw "File restore target is not a regular file: $original" }
        $backup = [IO.Path]::GetFullPath([string]$file.Backup)
        if (-not (Test-BackupFilePathAllowed -Path $backup -StateDirectory $StateDirectory)) { throw "Backup path is not a direct allowlisted transaction file: $backup" }
        Assert-ProtectedExistingFile -Path $backup
        if ([string]$file.BeforeHash -notmatch '^[0-9a-fA-F]{64}$' -or [string]$file.AfterHash -notmatch '^[0-9a-fA-F]{64}$') { throw "Invalid file hash in state: $original" }
        if ((Get-FileSha256 -Path $backup) -ine [string]$file.BeforeHash) { throw "Backup hash validation failed before restore: $backup" }
    }

    $seenServices = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($service in @($Candidate.Services)) {
        if (-not [bool]$service.ChangedByUs) { continue }
        if ([string]$service.Name -cne 'Wallpaper Engine Service' -or -not $seenServices.Add([string]$service.Name)) { throw "Service mutation is not uniquely allowlisted: $($service.Name)" }
        $expected = [pscustomobject]@{ StartMode = 'Manual'; Running = $false }
        if (-not (Test-ValueEqual -Left $service.Target -Right $expected)) { throw 'Wallpaper Engine service target does not match the allowlist.' }
        if ([string]$service.Before.StartMode -notin @('Auto','Manual','Disabled') -or $service.Before.Running -isnot [bool]) { throw 'Wallpaper Engine service Before state is invalid.' }
    }

    $seenTasks = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($task in @($Candidate.Tasks)) {
        if (-not [bool]$task.ChangedByUs) { continue }
        $taskIdentity = '{0}|{1}' -f [string]$task.TaskPath, [string]$task.TaskName
        if ($task.TaskPath -cne '\' -or $task.TaskName -cne 'nefarius_VirtualPad_Updater' -or -not $seenTasks.Add($taskIdentity)) { throw "Scheduled-task mutation is not uniquely allowlisted: $taskIdentity" }
        if (-not (Test-ValueEqual -Left $task.Target -Right ([pscustomobject]@{ Enabled = $false }))) { throw 'Scheduled-task target does not match the allowlist.' }
        if ($task.Before.Enabled -isnot [bool]) { throw 'Scheduled-task Before state is invalid.' }
    }

    foreach ($move in @($Candidate.MovedDirectories)) {
        if (-not [bool]$move.ChangedByUs) { continue }
        $expectedOriginal = Join-Path $programFilesRoot 'NVIDIA Corporation\NvContainer\plugins\SPUser'
        $expectedDisabled = Join-Path (Split-Path -Parent $expectedOriginal) ("SPUser.disabled-by-majesticboost-$($Candidate.OperationId)")
        if ([IO.Path]::GetFullPath([string]$move.Original) -ine $expectedOriginal -or [IO.Path]::GetFullPath([string]$move.Disabled) -ine $expectedDisabled) {
            throw 'Moved-directory mutation is not allowlisted for this OperationId.'
        }
        if (-not (Test-ValueEqual -Left $move.Target -Right ([pscustomobject]@{ OriginalExists = $false; DisabledExists = $true }))) { throw 'Moved-directory target does not match the allowlist.' }
        Assert-NoReparsePath -Path (Split-Path -Parent $expectedOriginal) -StopAt $programFilesRoot
        foreach ($movePath in @($expectedOriginal, $expectedDisabled)) {
            if (Test-Path -LiteralPath $movePath) {
                $moveItem = Get-Item -LiteralPath $movePath -Force
                if (($moveItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or -not $moveItem.PSIsContainer) { throw "Moved-directory endpoint is unsafe: $movePath" }
            }
        }
    }

    if (($Candidate.PSObject.Properties.Name -contains 'Defender') -and $Candidate.Defender) {
        $defenderTargets = @{ PUAProtection = 1; MAPSReporting = 2; SubmitSamplesConsent = 1; DisableRemovableDriveScanning = $false }
        $seenDefender = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($setting in @($Candidate.Defender.Settings)) {
            if (-not [bool]$setting.ChangedByUs) { continue }
            $name = [string]$setting.Name
            if (-not $defenderTargets.ContainsKey($name) -or -not $seenDefender.Add($name)) { throw "Defender mutation is not uniquely allowlisted: $name" }
            if (-not (Test-ValueEqual -Left $setting.Target -Right $defenderTargets[$name])) { throw "Defender target does not match the allowlist: $name" }
            if ($name -eq 'DisableRemovableDriveScanning') {
                if ($setting.Before -isnot [bool]) { throw 'Defender boolean Before state is invalid.' }
            }
            elseif ([int64]$setting.Before -lt 0 -or [int64]$setting.Before -gt 3) { throw "Defender Before state is invalid: $name" }
        }
    }

    if (-not ($Candidate.PSObject.Properties.Name -contains 'Power') -or $null -eq $Candidate.Power) { throw 'State Power tracking is missing.' }
    foreach ($powerProperty in @('Before','Target','ChangedByUs','SchemeCreated')) {
        if (-not ($Candidate.Power.PSObject.Properties.Name -contains $powerProperty)) { throw "State Power tracking is missing $powerProperty." }
    }
    if ([string]$Candidate.Power.Before -ine [string]$Candidate.OriginalPowerScheme -or [string]$Candidate.Power.Target -ine [string]$Candidate.MaxPowerScheme) {
        throw 'State Power tracking does not match the top-level GUIDs.'
    }
    if ($Candidate.Power.ChangedByUs -isnot [bool] -or $Candidate.Power.SchemeCreated -isnot [bool]) { throw 'State Power flags must be Boolean.' }
    if (($Candidate.PSObject.Properties.Name -contains 'PowerSchemeCreated') -and [bool]$Candidate.PowerSchemeCreated -ne [bool]$Candidate.Power.SchemeCreated) {
        throw 'State power scheme creation flags do not match.'
    }
    if ([bool]$Candidate.Power.ChangedByUs -and -not [bool]$Candidate.Power.SchemeCreated) { throw 'A changed power plan must be marked as created by this transaction.' }
}

function Get-RegistrySnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $keyExists = Test-Path -LiteralPath $Path
    $valueExists = $false
    $kind = $null
    $value = $null
    if ($keyExists) {
        $key = Get-Item -LiteralPath $Path
        $valueExists = @($key.GetValueNames()) -contains $Name
        if ($valueExists) {
            $kind = $key.GetValueKind($Name).ToString()
            $value = $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        }
    }
    return [pscustomobject]@{
        KeyExists = [bool]$keyExists
        Exists = [bool]$valueExists
        Kind = $kind
        Value = $value
    }
}

function Test-RegistrySnapshotEqual {
    param($Left, $Right)
    if ([bool]$Left.Exists -ne [bool]$Right.Exists) { return $false }
    if (-not [bool]$Left.Exists) { return $true }
    return ([string]$Left.Kind -ceq [string]$Right.Kind) -and
        (Test-ValueEqual -Left $Left.Value -Right $Right.Value)
}

function Restore-RegistrySnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)]$Snapshot
    )
    if ([bool]$Snapshot.Exists) {
        if (-not (Test-Path -LiteralPath $Path)) {
            New-Item -Path $Path -Force | Out-Null
        }
        $propertyType = switch ([string]$Snapshot.Kind) {
            'DWord' { 'DWord' }
            'QWord' { 'QWord' }
            'ExpandString' { 'ExpandString' }
            'MultiString' { 'MultiString' }
            'Binary' { 'Binary' }
            default { 'String' }
        }
        New-ItemProperty -Path $Path -Name $Name -Value $Snapshot.Value -PropertyType $propertyType -Force | Out-Null
    }
    elseif (Test-Path -LiteralPath $Path) {
        Remove-ItemProperty -Path $Path -Name $Name -ErrorAction SilentlyContinue
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Restore-FileAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Backup,
        [Parameter(Mandatory = $true)][string]$Original,
        [Parameter(Mandatory = $true)][string]$ExpectedHash
    )
    $directory = Split-Path -Parent $Original
    $temporary = Join-Path $directory ('.majesticboost-restore-{0}.tmp' -f [Guid]::NewGuid().ToString('N'))
    try {
        Copy-Item -LiteralPath $Backup -Destination $temporary -Force
        if ((Get-FileSha256 -Path $temporary) -ne $ExpectedHash) {
            throw "Prepared restore hash mismatch for $Original"
        }
        Replace-FileWithoutRetainedBackup -Source $temporary -Destination $Original
    }
    finally {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
}

function Get-ServiceSnapshot {
    param([Parameter(Mandatory = $true)][string]$Name)
    $escapedName = $Name.Replace("'", "''")
    $service = CimCmdlets\Get-CimInstance Win32_Service -Filter "Name='$escapedName'" -ErrorAction SilentlyContinue
    if (-not $service) { return $null }
    return [pscustomobject]@{
        StartMode = [string]$service.StartMode
        Running = ([string]$service.State -eq 'Running')
    }
}

function Get-TaskSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$TaskName,
        [Parameter(Mandatory = $true)][string]$TaskPath
    )
    $task = ScheduledTasks\Get-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -ErrorAction SilentlyContinue
    if (-not $task) { return $null }
    return [pscustomobject]@{ Enabled = ([string]$task.State -ne 'Disabled') }
}

function Get-DefenderSnapshot {
    $preference = Defender\Get-MpPreference -ErrorAction Stop
    return [pscustomobject]@{
        PUAProtection = [int]$preference.PUAProtection
        MAPSReporting = [int]$preference.MAPSReporting
        SubmitSamplesConsent = [int]$preference.SubmitSamplesConsent
        DisableRemovableDriveScanning = [bool]$preference.DisableRemovableDriveScanning
    }
}

function Test-EntryShouldRestore {
    param([Parameter(Mandatory = $true)]$Entry)
    if (-not ($Entry.PSObject.Properties.Name -contains 'ChangedByUs') -or -not [bool]$Entry.ChangedByUs) { return $false }
    # ChangedByUs is persisted before mutation.  Do not rely on Applied here:
    # a process/power loss can happen after the mutation but before its final
    # verification flag is saved.  The current-vs-Target guard below each
    # restore operation remains the authority.
    if (($Entry.PSObject.Properties.Name -contains 'Restored') -and [bool]$Entry.Restored) { return $false }
    return $true
}

function Set-EntryRestored {
    param([Parameter(Mandatory = $true)]$Entry)
    Set-ObjectProperty -Object $Entry -Name 'Restored' -Value $true
    Set-ObjectProperty -Object $Entry -Name 'RestoredAt' -Value (Get-Date).ToString('o')
}

function Add-RestoreConflict {
    param([Parameter(Mandatory = $true)][string]$Message)
    $script:restoreConflicts.Add($Message) | Out-Null
    Write-Warning $Message
}

function Add-RestoreError {
    param([Parameter(Mandatory = $true)][string]$Message)
    $script:restoreErrors.Add($Message) | Out-Null
    Write-Warning $Message
}

$transactionLock = $null
$transcriptStarted = $false
$state = $null
$resolvedStatePath = $null
$exitCode = 0
$restoreErrors = New-Object System.Collections.Generic.List[string]
$restoreConflicts = New-Object System.Collections.Generic.List[string]

try {
    Initialize-ProtectedStateStorage
    if ($storageHadUntrustedWriteBeforeProtection) {
        $transactionLock = Enter-TransactionLock
        $exitCode = 1
        $quarantineResult = [ordered]@{
            Status = 'QuarantinedUntrustedState'
            OperationId = $null
            StateFile = $null
            RebootRecommended = $false
            Errors = @('Automatic rollback was refused because the existing rollback metadata came from historically user-writable storage. Manual review is required.')
            Conflicts = @('An existing rollback pointer from historically user-writable storage was quarantined without reading or applying it.')
        }
        Write-ResultJson -Value $quarantineResult -Depth 8
    }
    else {
    if (-not $StatePath) { throw 'StatePath is required and must exactly match the protected latest-state pointer.' }
    $pointerText = (Get-Content -LiteralPath $latestStatePointer -Raw -Encoding UTF8).Trim()
    if (-not $pointerText) { throw 'The MAX FPS state pointer is empty.' }
    $pointerPath = [IO.Path]::GetFullPath($pointerText)
    $requestedPath = [IO.Path]::GetFullPath($StatePath)
    if ($requestedPath -ine $pointerPath) { throw 'StatePath does not exactly match the protected latest-state pointer.' }
    if (-not (Test-StateProvenanceTrusted -Path $requestedPath)) {
        throw 'Pointed transaction directory/state.json has untrusted ACL provenance; automatic rollback was refused.'
    }
    $resolvedStatePath = Get-ValidatedStatePath -Path $requestedPath
    $stateDirectory = Split-Path -Parent $resolvedStatePath
    $state = Get-Content -LiteralPath $resolvedStatePath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-MutationStateSchema -Candidate $state -ExpectedStatePath $resolvedStatePath -StateDirectory $stateDirectory
    $stateMutationsStarted = if ($state.PSObject.Properties.Name -contains 'MutationsStarted') { [bool]$state.MutationsStarted } else { $true }
    $statePhase = if ($state.PSObject.Properties.Name -contains 'Phase') { [string]$state.Phase } else { 'LegacyV2' }
    $powerCreationUncertain = $stateMutationsStarted -and $statePhase -in @('PowerPlanCreationPending','PowerPlanCreationAmbiguous')
    $validatedPointerHash = Get-FileSha256 -Path $latestStatePointer
    $validatedStateHash = Get-FileSha256 -Path $resolvedStatePath

    # Lock only after the complete read-only mutation preflight.  Then verify
    # the protected pointer/state did not change in the validation-to-lock gap.
    $transactionLock = Enter-TransactionLock
    Assert-ProtectedExistingFile -Path $latestStatePointer
    Assert-ProtectedExistingFile -Path $resolvedStatePath
    if ((Get-FileSha256 -Path $latestStatePointer) -ne $validatedPointerHash -or
        (Get-FileSha256 -Path $resolvedStatePath) -ne $validatedStateHash) {
        throw 'Protected rollback state changed during preflight; no settings were modified.'
    }
    $lockedPointer = (Get-Content -LiteralPath $latestStatePointer -Raw -Encoding UTF8).Trim()
    if ([IO.Path]::GetFullPath($lockedPointer) -ine $resolvedStatePath) { throw 'Protected latest-state pointer changed during preflight.' }

    function Save-State {
        Write-ProtectedJsonAtomic -Path $resolvedStatePath -Value $state -Depth 18
    }

    function Remove-MatchingStatePointer {
        if (-not (Test-Path -LiteralPath $latestStatePointer -PathType Leaf)) { return }
        Assert-ProtectedExistingFile -Path $latestStatePointer
        $currentPointer = (Get-Content -LiteralPath $latestStatePointer -Raw -Encoding UTF8).Trim()
        if ($currentPointer -and ([IO.Path]::GetFullPath($currentPointer) -eq $resolvedStatePath)) {
            Remove-Item -LiteralPath $latestStatePointer -Force
        }
    }

    if (-not $stateMutationsStarted) {
        $state.Status = 'AbortedNoChanges'
        Set-ObjectProperty -Object $state -Name 'Phase' -Value 'AbortedNoChanges'
        Set-ObjectProperty -Object $state -Name 'RestoredAt' -Value (Get-Date).ToString('o')
        Save-State
        Remove-MatchingStatePointer
        $noChangeResult = [ordered]@{
            Status = 'AbortedNoChanges'
            OperationId = [string]$state.OperationId
            RestoredAt = $state.RestoredAt
            StateFile = $resolvedStatePath
            RebootRecommended = $false
            Errors = @()
            Conflicts = @()
        }
        Write-ResultJson -Value $noChangeResult -Depth 8
    }
    elseif (($state.PSObject.Properties.Name -contains 'RestoredAt') -and $state.RestoredAt) {
        Remove-MatchingStatePointer
        $alreadyResult = [ordered]@{
            Status = 'AlreadyRestored'
            OperationId = $(if ($state.PSObject.Properties.Name -contains 'OperationId') { $state.OperationId } else { $null })
            RestoredAt = $state.RestoredAt
            StateFile = $resolvedStatePath
            RebootRecommended = $true
        }
        Write-ResultJson -Value $alreadyResult -Depth 8
    }
    else {
        if (-not ($state.PSObject.Properties.Name -contains 'Version') -or [int]$state.Version -lt 2) {
            throw 'Legacy state must first be adopted with MaxFPS-Apply.ps1 -AdoptExistingState before safe restoration.'
        }

        $logPath = Join-Path $stateDirectory 'restore.log'
        Start-Transcript -Path $logPath -Append -Force | Out-Null
        $transcriptStarted = $true
        Set-ProtectedFileSecurity -Path $logPath
        $state.Status = 'Restoring'
        Set-ObjectProperty -Object $state -Name 'RestoreStartedAt' -Value (Get-Date).ToString('o')
        Save-State

        if ($powerCreationUncertain) {
            $candidates = if (($state.PSObject.Properties.Name -contains 'UnknownCreatedPowerSchemes') -and @($state.UnknownCreatedPowerSchemes).Count -gt 0) {
                @($state.UnknownCreatedPowerSchemes) -join ', '
            }
            else { 'unknown (apply stopped during power-plan duplication)' }
            Add-RestoreConflict -Message "Power-plan creation could not be attributed safely; candidate schemes were preserved for manual review: $candidates"
        }

        foreach ($entry in @($state.Registry)) {
            if (-not (Test-EntryShouldRestore -Entry $entry)) { continue }
            try {
                $current = Get-RegistrySnapshot -Path ([string]$entry.Path) -Name ([string]$entry.Name)
                if (Test-RegistrySnapshotEqual -Left $current -Right $entry.Before) {
                    Set-EntryRestored -Entry $entry
                    Save-State
                    continue
                }
                if (-not (Test-RegistrySnapshotEqual -Left $current -Right $entry.Target)) {
                    Add-RestoreConflict -Message "Registry value changed after Boost and was preserved: $($entry.Path)::$($entry.Name)"
                    continue
                }
                $registryBeforeRestore = Get-RegistrySnapshot -Path ([string]$entry.Path) -Name ([string]$entry.Name)
                if (-not (Test-RegistrySnapshotEqual -Left $registryBeforeRestore -Right $entry.Target)) {
                    Add-RestoreConflict -Message "Registry value changed during rollback preparation and was preserved: $($entry.Path)::$($entry.Name)"
                    continue
                }
                Restore-RegistrySnapshot -Path ([string]$entry.Path) -Name ([string]$entry.Name) -Snapshot $entry.Before
                $after = Get-RegistrySnapshot -Path ([string]$entry.Path) -Name ([string]$entry.Name)
                if (-not (Test-RegistrySnapshotEqual -Left $after -Right $entry.Before)) {
                    throw 'Registry restore verification failed.'
                }
                Set-EntryRestored -Entry $entry
                Save-State
            }
            catch {
                Add-RestoreError -Message "Registry restore failed for $($entry.Path)::$($entry.Name): $($_.Exception.Message)"
            }
        }

        if (($state.PSObject.Properties.Name -contains 'Defender') -and $state.Defender) {
            $defenderSettings = @($state.Defender.Settings)
            foreach ($setting in $defenderSettings) {
                if (-not (Test-EntryShouldRestore -Entry $setting)) { continue }
                try {
                    $currentDefender = Get-DefenderSnapshot
                    $currentValue = $currentDefender.([string]$setting.Name)
                    if (Test-ValueEqual -Left $currentValue -Right $setting.Before) {
                        Set-EntryRestored -Entry $setting
                        Save-State
                        continue
                    }
                    if (-not (Test-ValueEqual -Left $currentValue -Right $setting.Target)) {
                        Add-RestoreConflict -Message "Defender setting changed after Boost and was preserved: $($setting.Name)"
                        continue
                    }
                    $defenderBeforeRestore = Get-DefenderSnapshot
                    if (-not (Test-ValueEqual -Left $defenderBeforeRestore.([string]$setting.Name) -Right $setting.Target)) {
                        Add-RestoreConflict -Message "Defender setting changed during rollback preparation and was preserved: $($setting.Name)"
                        continue
                    }
                    switch ([string]$setting.Name) {
                        'PUAProtection' { Defender\Set-MpPreference -PUAProtection ([int]$setting.Before) }
                        'MAPSReporting' { Defender\Set-MpPreference -MAPSReporting ([int]$setting.Before) }
                        'SubmitSamplesConsent' { Defender\Set-MpPreference -SubmitSamplesConsent ([int]$setting.Before) }
                        'DisableRemovableDriveScanning' { Defender\Set-MpPreference -DisableRemovableDriveScanning ([bool]$setting.Before) }
                        default { throw "Unsupported Defender state property: $($setting.Name)" }
                    }
                    $verifiedDefender = Get-DefenderSnapshot
                    if (-not (Test-ValueEqual -Left $verifiedDefender.([string]$setting.Name) -Right $setting.Before)) {
                        throw 'Defender restore verification failed.'
                    }
                    Set-EntryRestored -Entry $setting
                    Save-State
                }
                catch {
                    # Defender failures are isolated so registry, files, services, tasks and power still roll back.
                    Add-RestoreError -Message "Defender restore failed for $($setting.Name): $($_.Exception.Message)"
                }
            }
        }

        foreach ($file in @($state.Files)) {
            if (-not (Test-EntryShouldRestore -Entry $file)) { continue }
            try {
                Assert-NoReparsePath -Path ([string]$file.Original) -StopAt ([Environment]::GetFolderPath('UserProfile'))
                if (-not (Test-BackupFilePathAllowed -Path ([string]$file.Backup) -StateDirectory $stateDirectory)) {
                    throw "Backup file is outside the transaction directory: $($file.Backup)"
                }
                $backupHash = Get-FileSha256 -Path ([string]$file.Backup)
                if (-not $backupHash -or $backupHash -ne [string]$file.BeforeHash) {
                    throw 'Backup file is missing or its SHA-256 does not match BeforeHash.'
                }
                $currentHash = Get-FileSha256 -Path ([string]$file.Original)
                if ($currentHash -eq [string]$file.BeforeHash) {
                    Set-EntryRestored -Entry $file
                    Save-State
                    continue
                }
                if ($currentHash -ne [string]$file.AfterHash) {
                    Add-RestoreConflict -Message "File changed after Boost and was preserved: $($file.Original)"
                    continue
                }
                Assert-NoReparsePath -Path ([string]$file.Original) -StopAt ([Environment]::GetFolderPath('UserProfile'))
                if ((Get-FileSha256 -Path ([string]$file.Original)) -ne [string]$file.AfterHash) {
                    Add-RestoreConflict -Message "File changed during rollback preparation and was preserved: $($file.Original)"
                    continue
                }
                Restore-FileAtomic -Backup ([string]$file.Backup) -Original ([string]$file.Original) -ExpectedHash ([string]$file.BeforeHash)
                if ((Get-FileSha256 -Path ([string]$file.Original)) -ne [string]$file.BeforeHash) {
                    throw 'File restore verification failed.'
                }
                Set-EntryRestored -Entry $file
                Save-State
            }
            catch {
                Add-RestoreError -Message "File restore failed for $($file.Original): $($_.Exception.Message)"
            }
        }

        foreach ($service in @($state.Services)) {
            if (-not (Test-EntryShouldRestore -Entry $service)) { continue }
            try {
                $current = Get-ServiceSnapshot -Name ([string]$service.Name)
                if ($null -eq $current) {
                    Add-RestoreConflict -Message "Service no longer exists and was skipped: $($service.Name)"
                    continue
                }
                $startupType = switch ([string]$service.Before.StartMode) {
                    'Auto' { 'Automatic' }
                    'Disabled' { 'Disabled' }
                    default { 'Manual' }
                }
                $startModeConflict = $false
                $runningConflict = $false
                $expectedStartMode = [string]$current.StartMode
                if ([string]$current.StartMode -eq [string]$service.Target.StartMode) {
                    Set-Service -Name ([string]$service.Name) -StartupType $startupType -ErrorAction Stop
                    $expectedStartMode = [string]$service.Before.StartMode
                }
                elseif ([string]$current.StartMode -ne [string]$service.Before.StartMode) {
                    $startModeConflict = $true
                    Add-RestoreConflict -Message "Service startup mode changed after Boost and was preserved: $($service.Name)"
                }

                $afterStartup = Get-ServiceSnapshot -Name ([string]$service.Name)
                $expectedRunning = [bool]$afterStartup.Running
                if ([bool]$afterStartup.Running -eq [bool]$service.Target.Running) {
                    if ([bool]$service.Before.Running) {
                        Start-Service -Name ([string]$service.Name) -ErrorAction Stop
                    }
                    else {
                        Stop-Service -Name ([string]$service.Name) -Force -ErrorAction Stop
                    }
                    $expectedRunning = [bool]$service.Before.Running
                }
                elseif ([bool]$afterStartup.Running -ne [bool]$service.Before.Running) {
                    $runningConflict = $true
                    Add-RestoreConflict -Message "Service running state changed after Boost and was preserved: $($service.Name)"
                }

                $verifiedService = Get-ServiceSnapshot -Name ([string]$service.Name)
                if ([string]$verifiedService.StartMode -ne $expectedStartMode -or [bool]$verifiedService.Running -ne $expectedRunning) {
                    throw 'Component-wise service restore verification failed.'
                }
                if ($startModeConflict -or $runningConflict) {
                    Set-ObjectProperty -Object $service -Name 'ComponentConflict' -Value $true
                }
                Set-EntryRestored -Entry $service
                Save-State
            }
            catch {
                Add-RestoreError -Message "Service restore failed for $($service.Name): $($_.Exception.Message)"
            }
        }

        foreach ($task in @($state.Tasks)) {
            if (-not (Test-EntryShouldRestore -Entry $task)) { continue }
            try {
                $current = Get-TaskSnapshot -TaskName ([string]$task.TaskName) -TaskPath ([string]$task.TaskPath)
                if ($null -eq $current) {
                    Add-RestoreConflict -Message "Scheduled task no longer exists and was skipped: $($task.TaskPath)$($task.TaskName)"
                    continue
                }
                if (Test-ValueEqual -Left $current -Right $task.Before) {
                    Set-EntryRestored -Entry $task
                    Save-State
                    continue
                }
                if (-not (Test-ValueEqual -Left $current -Right $task.Target)) {
                    Add-RestoreConflict -Message "Scheduled task changed after Boost and was preserved: $($task.TaskPath)$($task.TaskName)"
                    continue
                }
                $taskBeforeRestore = Get-TaskSnapshot -TaskName ([string]$task.TaskName) -TaskPath ([string]$task.TaskPath)
                if (-not (Test-ValueEqual -Left $taskBeforeRestore -Right $task.Target)) {
                    Add-RestoreConflict -Message "Scheduled task changed during rollback preparation and was preserved: $($task.TaskPath)$($task.TaskName)"
                    continue
                }
                if ([bool]$task.Before.Enabled) {
                    ScheduledTasks\Enable-ScheduledTask -TaskName ([string]$task.TaskName) -TaskPath ([string]$task.TaskPath) | Out-Null
                }
                else {
                    ScheduledTasks\Disable-ScheduledTask -TaskName ([string]$task.TaskName) -TaskPath ([string]$task.TaskPath) | Out-Null
                }
                if (-not (Test-ValueEqual -Left (Get-TaskSnapshot -TaskName ([string]$task.TaskName) -TaskPath ([string]$task.TaskPath)) -Right $task.Before)) {
                    throw 'Scheduled task restore verification failed.'
                }
                Set-EntryRestored -Entry $task
                Save-State
            }
            catch {
                Add-RestoreError -Message "Scheduled task restore failed for $($task.TaskPath)$($task.TaskName): $($_.Exception.Message)"
            }
        }

        foreach ($move in @($state.MovedDirectories)) {
            if (-not (Test-EntryShouldRestore -Entry $move)) { continue }
            try {
                $originalExists = Test-Path -LiteralPath ([string]$move.Original)
                $disabledExists = Test-Path -LiteralPath ([string]$move.Disabled)
                if ($originalExists -and -not $disabledExists) {
                    Set-EntryRestored -Entry $move
                    Save-State
                    continue
                }
                if ($originalExists -or -not $disabledExists) {
                    Add-RestoreConflict -Message "SPUser directory state is ambiguous and was preserved: $($move.Original)"
                    continue
                }
                Assert-NoReparsePath -Path (Split-Path -Parent ([string]$move.Original)) -StopAt $programFilesRoot
                $disabledItem = Get-Item -LiteralPath ([string]$move.Disabled) -Force
                if (-not $disabledItem.PSIsContainer -or (($disabledItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or (Test-Path -LiteralPath ([string]$move.Original))) {
                    Add-RestoreConflict -Message "SPUser directory changed during rollback preparation and was preserved: $($move.Original)"
                    continue
                }
                Move-Item -LiteralPath ([string]$move.Disabled) -Destination ([string]$move.Original)
                if (-not (Test-Path -LiteralPath ([string]$move.Original)) -or (Test-Path -LiteralPath ([string]$move.Disabled))) {
                    throw 'SPUser directory restore verification failed.'
                }
                Set-EntryRestored -Entry $move
                Save-State
            }
            catch {
                Add-RestoreError -Message "SPUser directory restore failed: $($_.Exception.Message)"
            }
        }

        if ($state.Power -and (Test-EntryShouldRestore -Entry $state.Power)) {
            try {
                $originalScheme = [string]$state.OriginalPowerScheme
                $maxScheme = [string]$state.MaxPowerScheme
                $schemeCreated = [bool]$state.Power.SchemeCreated
                $maxExists = Test-PowerSchemeExists -Guid $maxScheme
                if ($maxExists) {
                    $activeScheme = Get-ActivePowerScheme
                    if ($activeScheme -eq $maxScheme) {
                        if (-not (Test-PowerSchemeExists -Guid $originalScheme)) {
                            throw "Original power scheme is missing; active MAX FPS scheme was not deleted: $originalScheme"
                        }
                        [void](Invoke-PowerCfg -Arguments @('/setactive', $originalScheme))
                        if ((Get-ActivePowerScheme) -ne $originalScheme) { throw 'Original power scheme activation verification failed.' }
                    }
                    elseif ($activeScheme -ne $originalScheme) {
                        Add-RestoreConflict -Message "The active power plan is a user override and was preserved: $activeScheme"
                    }
                    if ($schemeCreated -and (Get-ActivePowerScheme) -ne $maxScheme) {
                        [void](Invoke-PowerCfg -Arguments @('/delete', $maxScheme))
                        if (Test-PowerSchemeExists -Guid $maxScheme) { throw 'MAX FPS power scheme deletion verification failed.' }
                    }
                }
                Set-ObjectProperty -Object $state.Power -Name 'Restored' -Value $true
                Set-ObjectProperty -Object $state.Power -Name 'RestoredAt' -Value (Get-Date).ToString('o')
                Save-State
            }
            catch {
                Add-RestoreError -Message "Power plan restore failed: $($_.Exception.Message)"
            }
        }

        if ($restoreErrors.Count -gt 0) {
            $state.Status = 'RestoreIncomplete'
            Set-ObjectProperty -Object $state -Name 'LastRestoreErrors' -Value @($restoreErrors)
            Save-State
            $exitCode = 1
            $resultStatus = 'RestoreIncomplete'
            $restoredAt = $null
        }
        else {
            $restoredAt = (Get-Date).ToString('o')
            $resultStatus = if ($restoreConflicts.Count -gt 0) { 'RestoredWithConflicts' } else { 'Restored' }
            $state.Status = $resultStatus
            Set-ObjectProperty -Object $state -Name 'RestoredAt' -Value $restoredAt
            Set-ObjectProperty -Object $state -Name 'RestoreConflicts' -Value @($restoreConflicts)
            Save-State
            Remove-MatchingStatePointer
        }

        $result = [ordered]@{
            Status = $resultStatus
            OperationId = $(if ($state.PSObject.Properties.Name -contains 'OperationId') { $state.OperationId } else { $null })
            RestoredAt = $restoredAt
            StateFile = $resolvedStatePath
            RebootRecommended = $true
            Errors = @($restoreErrors)
            Conflicts = @($restoreConflicts)
            DefenderRestored = ($null -eq $state.Defender) -or (@($state.Defender.Settings | Where-Object { $_.ChangedByUs -and -not $_.Restored }).Count -eq 0)
        }
        Write-ResultJson -Value $result -Depth 10
    }
    }
}
catch {
    $exitCode = 1
    try {
        $failedResult = [ordered]@{
            Status = 'Failed'
            OperationId = $(if ($state -and ($state.PSObject.Properties.Name -contains 'OperationId')) { $state.OperationId } else { $null })
            StateFile = $resolvedStatePath
            Error = $_.Exception.Message
            RebootRecommended = $false
        }
        Write-ResultJson -Value $failedResult -Depth 8
    }
    catch {}
    [Console]::Error.WriteLine($_.Exception.Message)
}
finally {
    if ($transcriptStarted) {
        try { Stop-Transcript | Out-Null } catch {}
    }
    if ($transactionLock) {
        $transactionLock.Dispose()
    }
}

exit $exitCode
