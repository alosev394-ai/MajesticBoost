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

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public sealed class MajesticBoostSlowDripServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly Thread worker;
    private readonly int chunks;
    private readonly int delayMilliseconds;

    public MajesticBoostSlowDripServer(int chunks, int delayMilliseconds)
    {
        this.chunks = chunks;
        this.delayMilliseconds = delayMilliseconds;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Address = "http://127.0.0.1:" + port + "/update-v2.json";
        worker = new Thread(Serve);
        worker.IsBackground = true;
        worker.Start();
    }

    public string Address { get; private set; }

    private void Serve()
    {
        try
        {
            using (TcpClient client = listener.AcceptTcpClient())
            using (NetworkStream stream = client.GetStream())
            {
                int matched = 0;
                byte[] terminator = new byte[] { 13, 10, 13, 10 };
                while (matched < terminator.Length)
                {
                    int value = stream.ReadByte();
                    if (value < 0)
                    {
                        return;
                    }
                    matched = value == terminator[matched]
                        ? matched + 1
                        : (value == terminator[0] ? 1 : 0);
                }

                byte[] header = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/json\r\n" +
                    "Content-Length: " + chunks + "\r\n" +
                    "Connection: close\r\n\r\n");
                stream.Write(header, 0, header.Length);
                stream.Flush();
                for (int index = 0; index < chunks; index++)
                {
                    Thread.Sleep(delayMilliseconds);
                    stream.WriteByte((byte)'x');
                    stream.Flush();
                }
            }
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        listener.Stop();
        if (worker.IsAlive)
        {
            worker.Join(2000);
        }
    }
}
'@

$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationPath) {
    $ApplicationPath = Join-Path $projectRoot 'dist\MajesticBoost.exe'
}
if (-not $InstallerPath) {
    $InstallerPath = Join-Path $projectRoot 'dist\MajesticBoost-Setup-1.6.2.exe'
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
$buildHeadAddressMethod = $overlayType.GetMethod('BuildRepositoryHeadRequestAddress', $staticFlags)
$parseHeadMethod = $overlayType.GetMethod('ParseRepositoryHeadCommit', $staticFlags)
$buildImmutableAddressMethod = $overlayType.GetMethod('BuildImmutableManifestAddress', $staticFlags)
$transientFailureMethod = $overlayType.GetMethod('IsTransientManifestFailure', $staticFlags)
$waitForRetryMethod = $overlayType.GetMethod('WaitForManifestRetry', $staticFlags)
$downloadSmallFileMethod = $overlayType.GetMethod('DownloadSmallFile', $staticFlags)
$createRequestMethod = $overlayType.GetMethod('CreateRequest', $staticFlags)
if (-not $openMethod -or -not $validateMethod -or -not $refreshMethod -or -not $lockMethod -or
    -not $buildHeadAddressMethod -or -not $parseHeadMethod -or -not $buildImmutableAddressMethod -or
    -not $transientFailureMethod -or -not $waitForRetryMethod -or -not $downloadSmallFileMethod -or
    -not $createRequestMethod) {
    throw 'Compiled updater validation helpers were not found.'
}

$headAddressArguments = New-Object 'object[]' 1
$headAddressArguments[0] = 'test-token-1'
$headAddress = [string]$buildHeadAddressMethod.Invoke($null, $headAddressArguments)
if ($headAddress -cne 'https://api.github.com/repos/alosev394-ai/MajesticBoost/git/ref/heads/main?mb=test-token-1') {
    throw "Repository-head cache-busting URL is unexpected: $headAddress"
}

$commitSha = '8a3231c14cf92876a62be73395ca8ec7fe86d9a6'
$headFixture = [Text.Encoding]::UTF8.GetBytes(
    '{"ref":"refs/heads/main","object":{"sha":"' + $commitSha + '","type":"commit"}}')
$parseArguments = New-Object 'object[]' 1
$parseArguments[0] = $headFixture
if ([string]$parseHeadMethod.Invoke($null, $parseArguments) -cne $commitSha) {
    throw 'Repository-head commit parsing failed.'
}

$immutableArguments = New-Object 'object[]' 2
$immutableArguments[0] = $commitSha
$immutableArguments[1] = 'update-v2.json'
$immutableAddress = [string]$buildImmutableAddressMethod.Invoke($null, $immutableArguments)
if ($immutableAddress -cne ('https://raw.githubusercontent.com/alosev394-ai/MajesticBoost/' + $commitSha + '/update-v2.json')) {
    throw "Immutable manifest URL is unexpected: $immutableAddress"
}

$transientArguments = New-Object 'object[]' 1
foreach ($status in @(
    [Net.WebExceptionStatus]::ConnectFailure,
    [Net.WebExceptionStatus]::ConnectionClosed,
    [Net.WebExceptionStatus]::NameResolutionFailure,
    [Net.WebExceptionStatus]::ReceiveFailure,
    [Net.WebExceptionStatus]::SendFailure,
    [Net.WebExceptionStatus]::Timeout
)) {
    $transientArguments[0] = [Net.WebException]::new(
        'temporary connection failure',
        $status)
    if (-not [bool]$transientFailureMethod.Invoke($null, $transientArguments)) {
        throw "A temporary $status failure is not eligible for a bounded retry."
    }
}
foreach ($status in @(
    [Net.WebExceptionStatus]::TrustFailure,
    [Net.WebExceptionStatus]::SecureChannelFailure
)) {
    $transientArguments[0] = [Net.WebException]::new(
        'certificate failure',
        $status)
    if ([bool]$transientFailureMethod.Invoke($null, $transientArguments)) {
        throw "A $status failure must not be retried as a transient outage."
    }
}

$budgetArguments = New-Object 'object[]' 3
$budgetArguments[0] = [Diagnostics.Stopwatch]::StartNew()
$budgetArguments[1] = 20000
$budgetArguments[2] = [Net.WebException]::new(
    'temporary connection failure',
    [Net.WebExceptionStatus]::ConnectFailure)
try {
    [void]$waitForRetryMethod.Invoke($null, $budgetArguments)
    throw 'A retry delay larger than the total startup budget was accepted.'
}
catch {
    $budgetFailure = $_.Exception
    while (($budgetFailure -is [Reflection.TargetInvocationException] -or
        $budgetFailure -is [Management.Automation.MethodInvocationException]) -and
        $budgetFailure.InnerException) {
        $budgetFailure = $budgetFailure.InnerException
    }
    if ($budgetFailure -isnot [Net.WebException] -or
        $budgetFailure.Status -ne [Net.WebExceptionStatus]::Timeout) {
        throw $budgetFailure
    }
}
finally {
    $budgetArguments[0].Stop()
}

$slowServer = New-Object MajesticBoostSlowDripServer -ArgumentList 5, 450
$slowTimer = [Diagnostics.Stopwatch]::StartNew()
$slowArguments = New-Object 'object[]' 5
$slowArguments[0] = $slowServer.Address
$slowArguments[1] = 16
$slowArguments[2] = 5000
$slowArguments[3] = $slowTimer
$slowArguments[4] = 900
try {
    [void]$downloadSmallFileMethod.Invoke($null, $slowArguments)
    throw 'A slow-drip response bypassed the hard update-check deadline.'
}
catch {
    $slowFailure = $_.Exception
    while (($slowFailure -is [Reflection.TargetInvocationException] -or
        $slowFailure -is [Management.Automation.MethodInvocationException]) -and
        $slowFailure.InnerException) {
        $slowFailure = $slowFailure.InnerException
    }
    if ($slowFailure -isnot [Net.WebException] -or
        $slowFailure.Status -ne [Net.WebExceptionStatus]::Timeout) {
        throw $slowFailure
    }
}
finally {
    $slowTimer.Stop()
    $slowServer.Dispose()
}

$requestArguments = New-Object 'object[]' 2
$requestArguments[0] = $headAddress
$requestArguments[1] = 5000
$request = [Net.HttpWebRequest]$createRequestMethod.Invoke($null, $requestArguments)
try {
    if ($request.CachePolicy.Level -cne [Net.Cache.HttpRequestCacheLevel]::NoCacheNoStore -or
        $request.Headers[[Net.HttpRequestHeader]::CacheControl] -cne 'no-cache' -or
        $request.Headers[[Net.HttpRequestHeader]::Pragma] -cne 'no-cache') {
        throw 'The compiled updater request does not bypass stale HTTP cache entries.'
    }
}
finally {
    $request.Abort()
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
    'ManifestFetchAttempts = 3',
    'ManifestRequestTimeoutMilliseconds = 5000',
    'ManifestTotalTimeoutMilliseconds = 20000',
    'BuildRepositoryHeadRequestAddress(',
    'ParseRepositoryHeadCommit(',
    'BuildImmutableManifestAddress(',
    'IsTransientManifestFailure(',
    'HttpRequestCacheLevel.NoCacheNoStore',
    'HttpRequestHeader.CacheControl',
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
