using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MajesticBoost
{
    internal enum PerformanceCaptureStatus
    {
        Completed,
        NoSupportedGame,
        ToolNotFound,
        ToolInvalid,
        AccessDenied,
        UacCancelled,
        GameExited,
        CaptureFailed,
        InvalidCapture,
        Cancelled
    }

    internal enum PerformanceCaptureStage
    {
        Preparing,
        Waiting,
        Capturing,
        Processing,
        Completed
    }

    internal sealed class PerformanceCaptureProgress
    {
        public PerformanceCaptureStage Stage;
        public int Percent;
        public int ElapsedSeconds;
        public int TotalSeconds;
        public string Message;
    }

    internal sealed class SupportedGameProcess
    {
        public int ProcessId;
        public string ProcessName;
        public DateTime StartTimeUtc;

        public string DisplayName
        {
            get
            {
                return string.Equals(
                    ProcessName,
                    "GTA5_Enhanced",
                    StringComparison.OrdinalIgnoreCase)
                    ? "GTA V Enhanced"
                    : "GTA V";
            }
        }
    }

    internal sealed class PerformanceCaptureAttemptResult
    {
        public PerformanceCaptureStatus Status;
        public string Message;
        public int? ExitCode;
        public bool UsedElevation;
        public string ToolPath;
        public string CsvPath;
        public SupportedGameProcess Target;
        public BoostPerformanceResult Performance;

        public bool CanRetryElevated
        {
            get
            {
                return Status == PerformanceCaptureStatus.AccessDenied &&
                       !UsedElevation &&
                       Target != null;
            }
        }
    }

    internal static class PerformanceCaptureService
    {
        internal const long ExpectedPresentMonSize = 956768;
        internal const string ExpectedPresentMonSha256 =
            "9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191";

        private const int DelaySeconds = 5;
        private const int CaptureSeconds = 60;
        private const int CaptureTimeoutGraceSeconds = 15;
        private const int MinimumFrames = 300;
        private const double MinimumDurationMilliseconds = 10000.0;
        private const int MaximumSavedCaptures = 20;

        private sealed class ToolResolution
        {
            public PerformanceCaptureStatus Status;
            public string Path;
            public string Message;
        }

        private sealed class FrameGroup
        {
            public FrameGroup()
            {
                FrameTimes = new List<double>();
            }

            public List<double> FrameTimes;
            public double DurationMilliseconds;
        }

        public static SupportedGameProcess FindRunningGame()
        {
            var candidates = new List<SupportedGameProcess>();
            AddRunningGames(candidates, "GTA5_Enhanced");
            AddRunningGames(candidates, "GTA5");
            return candidates
                .OrderByDescending(candidate => candidate.StartTimeUtc)
                .FirstOrDefault();
        }

        public static Task<PerformanceCaptureAttemptResult> CaptureRunningGameAsync(
            IProgress<PerformanceCaptureProgress> progress,
            CancellationToken cancellationToken)
        {
            SupportedGameProcess target = FindRunningGame();
            if (target == null)
            {
                return Task.FromResult(CreateFailure(
                    PerformanceCaptureStatus.NoSupportedGame,
                    "Сначала запустите GTA V или GTA V Enhanced.",
                    null,
                    false,
                    null,
                    null));
            }

            return CaptureAsync(target, false, progress, cancellationToken);
        }

        public static Task<PerformanceCaptureAttemptResult> RetryElevatedAsync(
            PerformanceCaptureAttemptResult deniedAttempt,
            IProgress<PerformanceCaptureProgress> progress,
            CancellationToken cancellationToken)
        {
            if (deniedAttempt == null ||
                deniedAttempt.Status != PerformanceCaptureStatus.AccessDenied ||
                deniedAttempt.Target == null)
            {
                return Task.FromResult(CreateFailure(
                    PerformanceCaptureStatus.CaptureFailed,
                    "Повтор с правами администратора доступен только после отказа в доступе.",
                    deniedAttempt == null ? null : deniedAttempt.Target,
                    true,
                    null,
                    null));
            }

            return CaptureAsync(
                deniedAttempt.Target,
                true,
                progress,
                cancellationToken);
        }

        public static async Task<PerformanceCaptureAttemptResult> CaptureAsync(
            SupportedGameProcess target,
            bool elevated,
            IProgress<PerformanceCaptureProgress> progress,
            CancellationToken cancellationToken)
        {
            if (target == null || !IsSameGameProcessRunning(target))
            {
                return CreateFailure(
                    PerformanceCaptureStatus.GameExited,
                    "Игра уже закрыта или была перезапущена.",
                    target,
                    elevated,
                    null,
                    null);
            }

            Report(
                progress,
                PerformanceCaptureStage.Preparing,
                0,
                0,
                "Проверяем компонент измерения.");

            ToolResolution tool = ResolveAndValidateTool();
            if (tool.Status != PerformanceCaptureStatus.Completed)
            {
                return CreateFailure(
                    tool.Status,
                    tool.Message,
                    target,
                    elevated,
                    tool.Path,
                    null);
            }
            if (elevated && !IsTrustedInstalledToolPath(tool.Path))
            {
                return CreateFailure(
                    PerformanceCaptureStatus.ToolInvalid,
                    "Повтор с UAC доступен только для проверенного компонента из папки установки Majestic Boost.",
                    target,
                    true,
                    tool.Path,
                    null);
            }

            string csvPath;
            string elevatedOutputPath = null;
            try
            {
                csvPath = CreateCapturePath();
                if (elevated)
                {
                    elevatedOutputPath = CreateElevatedCapturePath();
                }
            }
            catch (Exception ex)
            {
                return CreateFailure(
                    PerformanceCaptureStatus.CaptureFailed,
                    "Не удалось подготовить файл измерения: " + ex.Message,
                    target,
                    elevated,
                    tool.Path,
                    null);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return CreateFailure(
                    PerformanceCaptureStatus.Cancelled,
                    "Измерение отменено.",
                    target,
                    elevated,
                    tool.Path,
                    csvPath);
            }
            if (!IsSameGameProcessRunning(target))
            {
                return CreateFailure(
                    PerformanceCaptureStatus.GameExited,
                    "Игра закрылась до начала измерения.",
                    target,
                    elevated,
                    tool.Path,
                    csvPath);
            }

            // Validate immediately before every process launch. The installed copy
            // lives under Program Files, so a standard user cannot replace it
            // between this check and CreateProcess.
            ToolResolution launchValidation = ValidateTool(tool.Path);
            if (launchValidation.Status != PerformanceCaptureStatus.Completed)
            {
                return CreateFailure(
                    launchValidation.Status,
                    launchValidation.Message,
                    target,
                    elevated,
                    tool.Path,
                    csvPath);
            }

            string sessionName = "MajesticBoost_" + Guid.NewGuid().ToString("N");
            string captureOutputPath = elevated ? elevatedOutputPath : csvPath;
            string arguments = BuildArguments(target.ProcessId, captureOutputPath, sessionName);
            var startInfo = new ProcessStartInfo
            {
                FileName = tool.Path,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(tool.Path),
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (elevated)
            {
                startInfo.UseShellExecute = true;
                startInfo.Verb = "runas";
            }
            else
            {
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
            }

            Process process = null;
            Task<string> standardOutputTask = null;
            Task<string> standardErrorTask = null;
            try
            {
                process = new Process { StartInfo = startInfo };
                if (!process.Start())
                {
                    return CreateFailure(
                        PerformanceCaptureStatus.CaptureFailed,
                        "PresentMon не запустился.",
                        target,
                        elevated,
                        tool.Path,
                        csvPath);
                }

                if (!elevated)
                {
                    standardOutputTask = process.StandardOutput.ReadToEndAsync();
                    standardErrorTask = process.StandardError.ReadToEndAsync();
                }

                DateTime captureStartedUtc = DateTime.UtcNow;
                await WaitForCaptureAsync(
                    process,
                    captureStartedUtc,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                string standardOutput = standardOutputTask == null
                    ? string.Empty
                    : await standardOutputTask.ConfigureAwait(false);
                string standardError = standardErrorTask == null
                    ? string.Empty
                    : await standardErrorTask.ConfigureAwait(false);
                int exitCode = process.ExitCode;

                if (exitCode != 0)
                {
                    if (IsAccessDenied(exitCode, standardOutput, standardError))
                    {
                        return CreateFailure(
                            PerformanceCaptureStatus.AccessDenied,
                            elevated
                                ? "PresentMon не получил доступ даже после подтверждения UAC."
                                : "Для измерения FPS требуется разовое подтверждение UAC.",
                            target,
                            elevated,
                            tool.Path,
                            csvPath,
                            exitCode);
                    }

                    return CreateFailure(
                        IsSameGameProcessRunning(target)
                            ? PerformanceCaptureStatus.CaptureFailed
                            : PerformanceCaptureStatus.GameExited,
                        BuildProcessFailureMessage(exitCode, standardError, standardOutput),
                        target,
                        elevated,
                        tool.Path,
                        csvPath,
                        exitCode);
                }
                if (elevated)
                {
                    ValidateElevatedCaptureFile(elevatedOutputPath);
                    File.Copy(elevatedOutputPath, csvPath, false);
                }
            }
            catch (OperationCanceledException)
            {
                TryStopProcess(process);
                return CreateFailure(
                    PerformanceCaptureStatus.Cancelled,
                    "Измерение отменено.",
                    target,
                    elevated,
                    tool.Path,
                    csvPath);
            }
            catch (Win32Exception ex)
            {
                if (elevated && ex.NativeErrorCode == 1223)
                {
                    return CreateFailure(
                        PerformanceCaptureStatus.UacCancelled,
                        "Подтверждение UAC отменено. Boost продолжает работать без замера FPS.",
                        target,
                        true,
                        tool.Path,
                        csvPath);
                }

                return CreateFailure(
                    ex.NativeErrorCode == 5
                        ? PerformanceCaptureStatus.AccessDenied
                        : PerformanceCaptureStatus.CaptureFailed,
                    "Не удалось запустить PresentMon: " + ex.Message,
                    target,
                    elevated,
                    tool.Path,
                    csvPath,
                    ex.NativeErrorCode);
            }
            catch (Exception ex)
            {
                TryStopProcess(process);
                return CreateFailure(
                    PerformanceCaptureStatus.CaptureFailed,
                    "Ошибка измерения FPS: " + ex.Message,
                    target,
                    elevated,
                    tool.Path,
                    csvPath);
            }
            finally
            {
                if (process != null)
                {
                    process.Dispose();
                }
                if (!string.IsNullOrWhiteSpace(elevatedOutputPath))
                {
                    TryDeleteCaptureFile(elevatedOutputPath);
                }
            }

            Report(
                progress,
                PerformanceCaptureStage.Processing,
                96,
                DelaySeconds + CaptureSeconds,
                "Обрабатываем результаты.");

            PerformanceCaptureAttemptResult parsed = ParseCaptureCsv(
                csvPath,
                target,
                elevated,
                tool.Path);
            if (parsed.Status == PerformanceCaptureStatus.Completed)
            {
                Report(
                    progress,
                    PerformanceCaptureStage.Completed,
                    100,
                    DelaySeconds + CaptureSeconds,
                    "Измерение завершено.");
                PruneOldCaptures();
            }
            return parsed;
        }

        internal static PerformanceCaptureAttemptResult ParseCaptureCsvForTesting(
            string csvPath,
            int processId,
            string processName,
            DateTime startTimeUtc)
        {
            return ParseCaptureCsv(
                csvPath,
                new SupportedGameProcess
                {
                    ProcessId = processId,
                    ProcessName = processName,
                    StartTimeUtc = startTimeUtc
                },
                false,
                string.Empty);
        }

        private static async Task WaitForCaptureAsync(
            Process process,
            DateTime startedUtc,
            IProgress<PerformanceCaptureProgress> progress,
            CancellationToken cancellationToken)
        {
            int totalSeconds = DelaySeconds + CaptureSeconds;
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int elapsed = Math.Max(
                    0,
                    (int)Math.Floor((DateTime.UtcNow - startedUtc).TotalSeconds));
                if (elapsed > totalSeconds + CaptureTimeoutGraceSeconds)
                {
                    TryStopProcess(process);
                    throw new TimeoutException(
                        "PresentMon не завершил измерение за отведённое время.");
                }
                bool waiting = elapsed < DelaySeconds;
                int percent = Math.Min(
                    95,
                    Math.Max(1, (int)Math.Round(elapsed * 95.0 / totalSeconds)));
                Report(
                    progress,
                    waiting
                        ? PerformanceCaptureStage.Waiting
                        : PerformanceCaptureStage.Capturing,
                    percent,
                    elapsed,
                    waiting
                        ? "Подготавливаем стабильный замер."
                        : "Измеряем плавность игры.");

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            // Refresh exit information after asynchronous polling.
            process.WaitForExit();
        }

        private static PerformanceCaptureAttemptResult ParseCaptureCsv(
            string csvPath,
            SupportedGameProcess target,
            bool elevated,
            string toolPath)
        {
            if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
            {
                return CreateFailure(
                    PerformanceCaptureStatus.CaptureFailed,
                    "PresentMon не создал файл измерения.",
                    target,
                    elevated,
                    toolPath,
                    csvPath);
            }

            try
            {
                var groups = new Dictionary<string, FrameGroup>(StringComparer.OrdinalIgnoreCase);
                List<string> header = null;
                int frameTimeIndex = -1;
                int processIdIndex = -1;
                int swapChainIndex = -1;

                using (var reader = new StreamReader(
                    csvPath,
                    Encoding.UTF8,
                    true,
                    64 * 1024))
                {
                    foreach (List<string> row in ReadCsvRows(reader))
                    {
                        if (header == null)
                        {
                            int candidateFrameTimeIndex = FindColumn(row, "FrameTime");
                            if (candidateFrameTimeIndex < 0)
                            {
                                continue;
                            }

                            header = row;
                            frameTimeIndex = candidateFrameTimeIndex;
                            processIdIndex = FindColumn(row, "ProcessID", "ProcessId");
                            swapChainIndex = FindColumn(
                                row,
                                "SwapChainAddress",
                                "SwapChain",
                                "SwapChainId");
                            continue;
                        }

                        if (frameTimeIndex >= row.Count)
                        {
                            continue;
                        }
                        if (processIdIndex >= 0 && processIdIndex < row.Count)
                        {
                            int rowProcessId;
                            if (!int.TryParse(
                                    row[processIdIndex].Trim(),
                                    NumberStyles.Integer,
                                    CultureInfo.InvariantCulture,
                                    out rowProcessId) ||
                                rowProcessId != target.ProcessId)
                            {
                                continue;
                            }
                        }

                        double frameTime;
                        if (!double.TryParse(
                                row[frameTimeIndex].Trim(),
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out frameTime) ||
                            double.IsNaN(frameTime) ||
                            double.IsInfinity(frameTime) ||
                            frameTime <= 0.0 ||
                            frameTime > 10000.0)
                        {
                            continue;
                        }

                        string groupKey = "__default__";
                        if (swapChainIndex >= 0 &&
                            swapChainIndex < row.Count &&
                            !string.IsNullOrWhiteSpace(row[swapChainIndex]))
                        {
                            groupKey = row[swapChainIndex].Trim();
                        }

                        FrameGroup group;
                        if (!groups.TryGetValue(groupKey, out group))
                        {
                            group = new FrameGroup();
                            groups.Add(groupKey, group);
                        }
                        group.FrameTimes.Add(frameTime);
                        group.DurationMilliseconds += frameTime;
                    }
                }

                FrameGroup selected = groups.Values
                    .OrderByDescending(group => group.FrameTimes.Count)
                    .ThenByDescending(group => group.DurationMilliseconds)
                    .FirstOrDefault();
                if (selected == null || selected.FrameTimes.Count == 0)
                {
                    return CreateFailure(
                        PerformanceCaptureStatus.InvalidCapture,
                        "В файле PresentMon нет корректных кадров GTA V.",
                        target,
                        elevated,
                        toolPath,
                        csvPath);
                }
                if (selected.FrameTimes.Count < MinimumFrames ||
                    selected.DurationMilliseconds < MinimumDurationMilliseconds)
                {
                    return CreateFailure(
                        PerformanceCaptureStatus.InvalidCapture,
                        string.Format(
                            CultureInfo.CurrentCulture,
                            "Замер слишком короткий: {0} кадров, {1:0.0} с. Нужно минимум 300 кадров и 10 секунд.",
                            selected.FrameTimes.Count,
                            selected.DurationMilliseconds / 1000.0),
                        target,
                        elevated,
                        toolPath,
                        csvPath);
                }

                List<double> sorted = selected.FrameTimes
                    .OrderBy(value => value)
                    .ToList();
                double mean = selected.DurationMilliseconds / sorted.Count;
                double p95 = NearestRankPercentile(sorted, 0.95);
                double p99 = NearestRankPercentile(sorted, 0.99);
                var performance = new BoostPerformanceResult
                {
                    Available = true,
                    Error = string.Empty,
                    CapturedUtc = DateTime.UtcNow,
                    AverageFps = 1000.0 / mean,
                    OnePercentLowFps = 1000.0 / p99,
                    P95FrameTimeMs = p95,
                    P99FrameTimeMs = p99,
                    Frames = sorted.Count,
                    FramesOver50Ms = sorted.Count(value => value > 50.0),
                    FramesOver100Ms = sorted.Count(value => value > 100.0),
                    ProcessName = target.ProcessName,
                    CsvPath = csvPath
                };
                return new PerformanceCaptureAttemptResult
                {
                    Status = PerformanceCaptureStatus.Completed,
                    Message = "Замер FPS успешно завершён.",
                    UsedElevation = elevated,
                    ToolPath = toolPath,
                    CsvPath = csvPath,
                    Target = target,
                    Performance = performance
                };
            }
            catch (Exception ex)
            {
                return CreateFailure(
                    PerformanceCaptureStatus.InvalidCapture,
                    "Не удалось прочитать результат PresentMon: " + ex.Message,
                    target,
                    elevated,
                    toolPath,
                    csvPath);
            }
        }

        private static IEnumerable<List<string>> ReadCsvRows(TextReader reader)
        {
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            bool hasData = false;
            while (true)
            {
                int value = reader.Read();
                if (value < 0)
                {
                    if (hasData || field.Length > 0 || row.Count > 0)
                    {
                        row.Add(field.ToString());
                        yield return row;
                    }
                    yield break;
                }

                char character = (char)value;
                hasData = true;
                if (character == '"')
                {
                    if (quoted && reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                    continue;
                }

                if (character == ',' && !quoted)
                {
                    row.Add(field.ToString());
                    field.Length = 0;
                    continue;
                }

                if ((character == '\r' || character == '\n') && !quoted)
                {
                    if (character == '\r' && reader.Peek() == '\n')
                    {
                        reader.Read();
                    }
                    row.Add(field.ToString());
                    field.Length = 0;
                    hasData = false;
                    if (row.Count > 1 || row[0].Length > 0)
                    {
                        yield return row;
                    }
                    row = new List<string>();
                    continue;
                }

                field.Append(character);
            }
        }

        private static int FindColumn(
            IList<string> header,
            params string[] acceptedNames)
        {
            var names = new HashSet<string>(
                acceptedNames.Select(NormalizeColumn),
                StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < header.Count; index++)
            {
                if (names.Contains(NormalizeColumn(header[index])))
                {
                    return index;
                }
            }
            return -1;
        }

        private static string NormalizeColumn(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            var normalized = new StringBuilder(value.Length);
            foreach (char character in value.Trim().Trim('\uFEFF'))
            {
                if (char.IsLetterOrDigit(character))
                {
                    normalized.Append(char.ToLowerInvariant(character));
                }
            }
            return normalized.ToString();
        }

        private static double NearestRankPercentile(
            IList<double> sortedValues,
            double percentile)
        {
            int rank = (int)Math.Ceiling(percentile * sortedValues.Count);
            int index = Math.Max(0, Math.Min(sortedValues.Count - 1, rank - 1));
            return sortedValues[index];
        }

        private static ToolResolution ResolveAndValidateTool()
        {
            bool foundInvalid = false;
            string invalidPath = null;
            string invalidMessage = null;
            foreach (string candidate in GetToolCandidates())
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                ToolResolution validation = ValidateTool(candidate);
                if (validation.Status == PerformanceCaptureStatus.Completed)
                {
                    return validation;
                }
                foundInvalid = true;
                invalidPath = candidate;
                invalidMessage = validation.Message;
            }

            return new ToolResolution
            {
                Status = foundInvalid
                    ? PerformanceCaptureStatus.ToolInvalid
                    : PerformanceCaptureStatus.ToolNotFound,
                Path = invalidPath,
                Message = foundInvalid
                    ? invalidMessage
                    : "Компонент PresentMon не установлен. Переустановите Majestic Boost."
            };
        }

        private static ToolResolution ValidateTool(string path)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists)
                {
                    return new ToolResolution
                    {
                        Status = PerformanceCaptureStatus.ToolNotFound,
                        Path = path,
                        Message = "Компонент PresentMon не найден."
                    };
                }
                if (file.Length != ExpectedPresentMonSize)
                {
                    return new ToolResolution
                    {
                        Status = PerformanceCaptureStatus.ToolInvalid,
                        Path = path,
                        Message = "Размер PresentMon не совпадает с проверенной версией 2.5.1."
                    };
                }

                string hash;
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                using (var algorithm = SHA256.Create())
                {
                    hash = ToHex(algorithm.ComputeHash(stream));
                }
                if (!string.Equals(
                        hash,
                        ExpectedPresentMonSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new ToolResolution
                    {
                        Status = PerformanceCaptureStatus.ToolInvalid,
                        Path = path,
                        Message = "Контрольная сумма PresentMon не совпадает с проверенной версией 2.5.1."
                    };
                }

                return new ToolResolution
                {
                    Status = PerformanceCaptureStatus.Completed,
                    Path = path,
                    Message = string.Empty
                };
            }
            catch (Exception ex)
            {
                return new ToolResolution
                {
                    Status = PerformanceCaptureStatus.ToolInvalid,
                    Path = path,
                    Message = "PresentMon не прошёл проверку: " + ex.Message
                };
            }
        }

        private static IEnumerable<string> GetToolCandidates()
        {
            var candidates = new List<string>();
            AddProgramFilesCandidate(
                candidates,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            AddProgramFilesCandidate(
                candidates,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            try
            {
                candidates.Add(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Tools",
                    "PresentMon",
                    "PresentMon.exe"));
            }
            catch { }

            return candidates
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(SafeFullPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static void AddProgramFilesCandidate(
            ICollection<string> candidates,
            string programFiles)
        {
            if (string.IsNullOrWhiteSpace(programFiles))
            {
                return;
            }
            candidates.Add(Path.Combine(
                programFiles,
                "Majestic Boost",
                "Tools",
                "PresentMon",
                "PresentMon.exe"));
        }

        private static bool IsTrustedInstalledToolPath(string path)
        {
            string fullPath = SafeFullPath(path);
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return false;
            }
            foreach (string programFiles in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            })
            {
                if (string.IsNullOrWhiteSpace(programFiles))
                {
                    continue;
                }
                string expected = SafeFullPath(Path.Combine(
                    programFiles,
                    "Majestic Boost",
                    "Tools",
                    "PresentMon",
                    "PresentMon.exe"));
                if (!string.IsNullOrWhiteSpace(expected) &&
                    string.Equals(fullPath, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string SafeFullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return null;
            }
        }

        private static void AddRunningGames(
            ICollection<SupportedGameProcess> candidates,
            string processName)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                return;
            }

            foreach (Process process in processes)
            {
                using (process)
                {
                    try
                    {
                        candidates.Add(new SupportedGameProcess
                        {
                            ProcessId = process.Id,
                            ProcessName = process.ProcessName,
                            StartTimeUtc = process.StartTime.ToUniversalTime()
                        });
                    }
                    catch
                    {
                        // A process can exit while the snapshot is being built.
                    }
                }
            }
        }

        private static bool IsSameGameProcessRunning(SupportedGameProcess target)
        {
            if (target == null ||
                (!string.Equals(target.ProcessName, "GTA5", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(target.ProcessName, "GTA5_Enhanced", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            try
            {
                using (Process process = Process.GetProcessById(target.ProcessId))
                {
                    return string.Equals(
                               process.ProcessName,
                               target.ProcessName,
                               StringComparison.OrdinalIgnoreCase) &&
                           process.StartTime.ToUniversalTime() == target.StartTimeUtc;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string BuildArguments(
            int processId,
            string csvPath,
            string sessionName)
        {
            return string.Join(
                " ",
                new[]
                {
                    "--process_id",
                    processId.ToString(CultureInfo.InvariantCulture),
                    "--output_file",
                    QuoteArgument(csvPath),
                    "--delay",
                    DelaySeconds.ToString(CultureInfo.InvariantCulture),
                    "--timed",
                    CaptureSeconds.ToString(CultureInfo.InvariantCulture),
                    "--terminate_after_timed",
                    "--terminate_on_proc_exit",
                    "--no_console_stats",
                    "--no_track_input",
                    "--v2_metrics",
                    "--session_name",
                    QuoteArgument(sessionName)
                });
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty)
                .Replace("\"", "\\\"") + "\"";
        }

        private static string CreateCapturePath()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MajesticBoost",
                "Captures");
            Directory.CreateDirectory(directory);
            PruneOldCaptures();
            string fileName = string.Format(
                CultureInfo.InvariantCulture,
                "presentmon-{0:yyyyMMdd-HHmmss}-{1}.csv",
                DateTime.UtcNow,
                Guid.NewGuid().ToString("N"));
            return Path.Combine(directory, fileName);
        }

        private static string CreateElevatedCapturePath()
        {
            string captureDirectory = ResolveProtectedCaptureDirectory();
            return Path.Combine(
                captureDirectory,
                "MajesticBoost-PresentMon-" + Guid.NewGuid().ToString("N") + ".csv");
        }

        private static string ResolveProtectedCaptureDirectory()
        {
            string commonDataDirectory = SafeFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            if (string.IsNullOrWhiteSpace(commonDataDirectory))
            {
                throw new InvalidOperationException("Системная папка ProgramData недоступна.");
            }

            string productDirectory = SafeFullPath(Path.Combine(
                commonDataDirectory,
                "MajesticBoost"));
            string captureDirectory = SafeFullPath(Path.Combine(
                productDirectory ?? string.Empty,
                "Captures"));
            if (string.IsNullOrWhiteSpace(productDirectory) ||
                string.IsNullOrWhiteSpace(captureDirectory))
            {
                throw new InvalidOperationException("Защищённая папка измерений недоступна.");
            }

            string commonDataPrefix = commonDataDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string productPrefix = productDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!productDirectory.StartsWith(
                    commonDataPrefix,
                    StringComparison.OrdinalIgnoreCase) ||
                !captureDirectory.StartsWith(
                    productPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Папка измерений находится вне ProgramData.");
            }

            ValidateProtectedCaptureDirectory(commonDataDirectory, "ProgramData");
            ValidateProtectedCaptureDirectory(productDirectory, "MajesticBoost");
            ValidateProtectedCaptureDirectory(captureDirectory, "Captures");
            return captureDirectory;
        }

        private static void ValidateProtectedCaptureDirectory(string path, string name)
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists ||
                (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Защищённая папка " + name +
                    " отсутствует или не прошла проверку безопасности. Переустановите Majestic Boost.");
            }
        }

        private static void ValidateElevatedCaptureFile(string path)
        {
            string fullPath = SafeFullPath(path);
            string directoryPath = string.IsNullOrWhiteSpace(fullPath)
                ? null
                : SafeFullPath(Path.GetDirectoryName(fullPath));
            if (string.IsNullOrWhiteSpace(fullPath) ||
                string.IsNullOrWhiteSpace(directoryPath))
            {
                throw new InvalidOperationException("Файл измерения имеет недопустимый путь.");
            }

            string expectedDirectory = ResolveProtectedCaptureDirectory();
            string directoryPrefix = expectedDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var file = new FileInfo(fullPath);
            if (!file.Exists ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !string.Equals(
                    directoryPath,
                    expectedDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                !fullPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(
                    file.Name,
                    @"^MajesticBoost-PresentMon-[0-9a-f]{32}\.csv$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                throw new InvalidOperationException("Защищённый файл измерения не прошёл проверку безопасности.");
            }
        }

        private static void TryDeleteCaptureFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // The installer grants standard users no directory write access,
                // but capture files inherit a file-only Delete right for cleanup.
                // Uninstall still prunes an artifact that remains locked here.
            }
        }

        private static void PruneOldCaptures()
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MajesticBoost",
                    "Captures");
                var info = new DirectoryInfo(directory);
                if (!info.Exists)
                {
                    return;
                }
                string requiredPrefix = info.FullName
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                FileInfo[] captures = info.GetFiles("presentmon-*.csv")
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToArray();
                for (int index = MaximumSavedCaptures; index < captures.Length; index++)
                {
                    string fullPath = Path.GetFullPath(captures[index].FullName);
                    if (fullPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(fullPath); }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static bool IsAccessDenied(
            int exitCode,
            string standardOutput,
            string standardError)
        {
            if (exitCode == 5 || exitCode == 6)
            {
                return true;
            }
            string text = (standardError ?? string.Empty) + "\n" +
                          (standardOutput ?? string.Empty);
            return text.IndexOf("access denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildProcessFailureMessage(
            int exitCode,
            string standardError,
            string standardOutput)
        {
            string detail = FirstNonEmptyLine(standardError);
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = FirstNonEmptyLine(standardOutput);
            }
            if (detail.Length > 240)
            {
                detail = detail.Substring(0, 240);
            }
            return string.IsNullOrWhiteSpace(detail)
                ? "PresentMon завершился с кодом " +
                  exitCode.ToString(CultureInfo.InvariantCulture) + "."
                : "PresentMon: " + detail;
        }

        private static string FirstNonEmptyLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            return value
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0) ?? string.Empty;
        }

        private static void TryStopProcess(Process process)
        {
            if (process == null)
            {
                return;
            }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
            }
            catch { }
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private static void Report(
            IProgress<PerformanceCaptureProgress> progress,
            PerformanceCaptureStage stage,
            int percent,
            int elapsedSeconds,
            string message)
        {
            if (progress == null)
            {
                return;
            }
            progress.Report(new PerformanceCaptureProgress
            {
                Stage = stage,
                Percent = Math.Max(0, Math.Min(100, percent)),
                ElapsedSeconds = Math.Max(0, elapsedSeconds),
                TotalSeconds = DelaySeconds + CaptureSeconds,
                Message = message ?? string.Empty
            });
        }

        private static PerformanceCaptureAttemptResult CreateFailure(
            PerformanceCaptureStatus status,
            string message,
            SupportedGameProcess target,
            bool elevated,
            string toolPath,
            string csvPath,
            int? exitCode = null)
        {
            return new PerformanceCaptureAttemptResult
            {
                Status = status,
                Message = message ?? string.Empty,
                ExitCode = exitCode,
                UsedElevation = elevated,
                ToolPath = toolPath,
                CsvPath = csvPath,
                Target = target,
                Performance = new BoostPerformanceResult
                {
                    Available = false,
                    Error = message ?? string.Empty,
                    CapturedUtc = DateTime.UtcNow,
                    ProcessName = target == null ? string.Empty : target.ProcessName,
                    CsvPath = csvPath ?? string.Empty
                }
            };
        }
    }
}
