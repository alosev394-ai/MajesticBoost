[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$featuresPath = Join-Path $projectRoot 'MajesticBoost\BoostFeatures.cs'
$diagnosticsPath = Join-Path $projectRoot 'MajesticBoost\DiagnosticsFeatures.cs'
$features = [IO.File]::ReadAllText($featuresPath)

foreach ($required in @(
    'ReadProcessStandardOutputWithTimeout(',
    'process.StandardOutput.ReadToEndAsync()',
    'process.StandardError.ReadToEndAsync()',
    'process.WaitForExit(timeoutMilliseconds)',
    'TerminateTimedOutProcess(process)',
    'ProcessTerminationTimeoutMilliseconds'
)) {
    if (-not $features.Contains($required)) {
        throw "Power-plan process timeout contract is missing: $required"
    }
}

if ($features.Contains('process.StandardOutput.ReadToEnd();')) {
    throw 'Power-plan diagnostics still perform an unbounded synchronous output read.'
}

$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw ".NET Framework C# compiler not found: $compiler"
}

$testRoot = Join-Path $env:TEMP (
    'MajesticBoost-PowerPlanTimeout-' + [Guid]::NewGuid().ToString('N'))
$fixturePath = Join-Path $testRoot 'BoostFeaturesFixture.dll'
$harnessSourcePath = Join-Path $testRoot 'PowerPlanTimeoutHarness.cs'
$harnessPath = Join-Path $testRoot 'PowerPlanTimeoutHarness.exe'
$childPidPath = Join-Path $testRoot 'hanging-child.pid'
$runner = $null

try {
    [void](New-Item -ItemType Directory -Path $testRoot)

    $fixtureArguments = @(
        '/nologo',
        '/target:library',
        "/out:$fixturePath",
        '/reference:System.dll',
        '/reference:System.Core.dll',
        $featuresPath,
        $diagnosticsPath
    )
    & $compiler @fixtureArguments
    if ($LASTEXITCODE -ne 0 -or
        -not (Test-Path -LiteralPath $fixturePath)) {
        throw 'Could not compile the production BoostFeatures fixture.'
    }

    $harnessSource = @'
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;

internal static class PowerPlanTimeoutHarness
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 &&
            string.Equals(args[0], "--hang-child", StringComparison.Ordinal))
        {
            return RunHangingChild(args);
        }
        if (args.Length > 0 &&
            string.Equals(args[0], "--quick-child", StringComparison.Ordinal))
        {
            Console.Out.Write("ready");
            Console.Error.Write(new string('E', 256 * 1024));
            Console.Out.Flush();
            Console.Error.Flush();
            return 0;
        }

        if (args.Length != 2)
        {
            Console.Error.WriteLine("Expected fixture and PID-file paths.");
            return 2;
        }

        Assembly fixture = Assembly.Load(File.ReadAllBytes(args[0]));
        Type serviceType = fixture.GetType(
            "MajesticBoost.BoostPreflightService",
            true,
            false);
        MethodInfo readMethod = serviceType.GetMethod(
            "ReadProcessStandardOutputWithTimeout",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (readMethod == null)
        {
            Console.Error.WriteLine("Production timeout helper was not found.");
            return 3;
        }

        string executable = Assembly.GetExecutingAssembly().Location;
        var hangingStartInfo = CreateStartInfo(
            executable,
            "--hang-child " + Quote(args[1]));
        var timer = Stopwatch.StartNew();
        bool timedOut = false;
        try
        {
            readMethod.Invoke(
                null,
                new object[] { hangingStartInfo, 350 });
        }
        catch (TargetInvocationException ex)
        {
            timedOut = ex.InnerException is TimeoutException;
            if (!timedOut)
            {
                Console.Error.WriteLine(ex.InnerException ?? ex);
            }
        }
        timer.Stop();

        if (!timedOut)
        {
            Console.Error.WriteLine("The hanging child did not produce TimeoutException.");
            return 4;
        }
        if (timer.ElapsedMilliseconds < 250 ||
            timer.ElapsedMilliseconds > 4000)
        {
            Console.Error.WriteLine(
                "The timeout was not bounded: " +
                timer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                " ms.");
            return 5;
        }
        if (!File.Exists(args[1]))
        {
            Console.Error.WriteLine("The hanging child did not start.");
            return 6;
        }

        int childPid = int.Parse(
            File.ReadAllText(args[1]),
            CultureInfo.InvariantCulture);
        if (IsProcessAlive(childPid))
        {
            Console.Error.WriteLine("The timed-out child process is still alive.");
            return 7;
        }

        var quickStartInfo = CreateStartInfo(executable, "--quick-child");
        string output;
        try
        {
            output = (string)readMethod.Invoke(
                null,
                new object[] { quickStartInfo, 2000 });
        }
        catch (TargetInvocationException ex)
        {
            Console.Error.WriteLine(ex.InnerException ?? ex);
            return 8;
        }
        if (!string.Equals(output, "ready", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Successful output was not preserved.");
            return 9;
        }

        Console.Out.WriteLine(
            "Production process reader times out, terminates, and drains both pipes.");
        return 0;
    }

    private static int RunHangingChild(string[] args)
    {
        if (args.Length != 2)
        {
            return 10;
        }

        File.WriteAllText(
            args[1],
            Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
        Console.Out.Write(new string('O', 256 * 1024));
        Console.Error.Write(new string('E', 256 * 1024));
        Console.Out.Flush();
        Console.Error.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        string arguments)
    {
        return new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using (Process process = Process.GetProcessById(processId))
            {
                return !process.HasExited;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
'@
    [IO.File]::WriteAllText(
        $harnessSourcePath,
        $harnessSource,
        (New-Object Text.UTF8Encoding($false)))

    $harnessArguments = @(
        '/nologo',
        '/target:exe',
        "/out:$harnessPath",
        '/reference:System.dll',
        '/reference:System.Core.dll',
        $harnessSourcePath
    )
    & $compiler @harnessArguments
    if ($LASTEXITCODE -ne 0 -or
        -not (Test-Path -LiteralPath $harnessPath)) {
        throw 'Could not compile the power-plan timeout harness.'
    }

    $runnerStartInfo = New-Object Diagnostics.ProcessStartInfo
    $runnerStartInfo.FileName = $harnessPath
    $runnerStartInfo.Arguments =
        '"' + $fixturePath + '" "' + $childPidPath + '"'
    $runnerStartInfo.UseShellExecute = $false
    $runnerStartInfo.CreateNoWindow = $true
    $runnerStartInfo.RedirectStandardOutput = $true
    $runnerStartInfo.RedirectStandardError = $true

    $runner = New-Object Diagnostics.Process
    $runner.StartInfo = $runnerStartInfo
    if (-not $runner.Start()) {
        throw 'Could not start the power-plan timeout harness.'
    }
    $runnerOutputTask = $runner.StandardOutput.ReadToEndAsync()
    $runnerErrorTask = $runner.StandardError.ReadToEndAsync()
    if (-not $runner.WaitForExit(10000)) {
        try {
            $runner.Kill()
            [void]$runner.WaitForExit(3000)
        }
        catch {
        }
        throw 'Power-plan timeout regression test itself exceeded 10 seconds.'
    }

    $runnerOutput = $runnerOutputTask.Result
    $runnerError = $runnerErrorTask.Result
    if ($runner.ExitCode -ne 0) {
        throw (
            "Power-plan timeout harness failed with exit code " +
            "$($runner.ExitCode).`n$runnerOutput`n$runnerError")
    }

    $runnerOutput.Trim()
}
finally {
    if ($null -ne $runner) {
        try {
            if (-not $runner.HasExited) {
                $runner.Kill()
                [void]$runner.WaitForExit(3000)
            }
        }
        catch {
        }
        $runner.Dispose()
    }

    if (Test-Path -LiteralPath $childPidPath) {
        $childPidText = [IO.File]::ReadAllText($childPidPath).Trim()
        [int]$childPid = 0
        if ([int]::TryParse($childPidText, [ref]$childPid)) {
            $childProcess = Get-Process -Id $childPid -ErrorAction SilentlyContinue
            if ($null -ne $childProcess) {
                try {
                    if ([string]::Equals(
                        $childProcess.Path,
                        $harnessPath,
                        [StringComparison]::OrdinalIgnoreCase)) {
                        $childProcess.Kill()
                        [void]$childProcess.WaitForExit(3000)
                    }
                }
                finally {
                    $childProcess.Dispose()
                }
            }
        }
    }

    # The runner and both children use a per-test executable path. Sweep that
    # exact path so an exception between Process.Start and PID-file creation
    # cannot leave a test process behind.
    $cleanupTimer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $remainingProcesses = @(
            Get-Process -Name 'PowerPlanTimeoutHarness' -ErrorAction SilentlyContinue |
                Where-Object {
                    try {
                        [string]::Equals(
                            $_.Path,
                            $harnessPath,
                            [StringComparison]::OrdinalIgnoreCase)
                    }
                    catch {
                        $false
                    }
                }
        )
        foreach ($remainingProcess in $remainingProcesses) {
            try {
                $remainingProcess.Kill()
                [void]$remainingProcess.WaitForExit(500)
            }
            catch {
            }
            finally {
                $remainingProcess.Dispose()
            }
        }
        if ($remainingProcesses.Count -eq 0) {
            break
        }
        Start-Sleep -Milliseconds 25
    }
    while ($cleanupTimer.ElapsedMilliseconds -lt 3000)

    $leakedProcesses = @(
        Get-Process -Name 'PowerPlanTimeoutHarness' -ErrorAction SilentlyContinue |
            Where-Object {
                try {
                    [string]::Equals(
                        $_.Path,
                        $harnessPath,
                        [StringComparison]::OrdinalIgnoreCase)
                }
                catch {
                    $false
                }
            }
    )
    if ($leakedProcesses.Count -ne 0) {
        foreach ($leakedProcess in $leakedProcesses) {
            $leakedProcess.Dispose()
        }
        throw 'Power-plan timeout test left a child process running.'
    }

    if (Test-Path -LiteralPath $testRoot) {
        $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
        $resolvedTemp = [IO.Path]::GetFullPath($env:TEMP).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)
        $expectedPrefix = $resolvedTemp + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedRoot.StartsWith(
            $expectedPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected test path: $resolvedRoot"
        }
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
