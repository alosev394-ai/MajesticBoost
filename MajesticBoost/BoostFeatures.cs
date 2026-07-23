using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MajesticBoost
{
    internal enum BoostCheckSeverity
    {
        Pass,
        Info,
        Warning,
        Blocked,
        Unknown
    }

    internal sealed class BoostCheckResult
    {
        public string Id;
        public string Title;
        public string Detail;
        public BoostCheckSeverity Severity;
    }

    internal sealed class BoostPreflightReport
    {
        public BoostPreflightReport()
        {
            Checks = new List<BoostCheckResult>();
            CapturedUtc = DateTime.UtcNow;
        }

        public DateTime CapturedUtc;
        public List<BoostCheckResult> Checks;
        public long TotalMemoryBytes;
        public long AvailableMemoryBytes;
        public int RefreshRate;

        public bool HasBlockers
        {
            get { return Checks.Any(item => item.Severity == BoostCheckSeverity.Blocked); }
        }

        public bool HasWarnings
        {
            get
            {
                return Checks.Any(
                    item => item.Severity == BoostCheckSeverity.Warning ||
                            item.Severity == BoostCheckSeverity.Blocked);
            }
        }
    }

    internal sealed class BoostCenterSettings
    {
        public bool AutoBoost;
        public bool CheckBeforeBoost;
        public bool KeepOneDrive;
        public bool KeepTeams;
        public bool KeepWallpaper;
        public bool KeepNvidiaOverlay;

        public BoostCenterSettings Clone()
        {
            return new BoostCenterSettings
            {
                AutoBoost = AutoBoost,
                CheckBeforeBoost = CheckBeforeBoost,
                KeepOneDrive = KeepOneDrive,
                KeepTeams = KeepTeams,
                KeepWallpaper = KeepWallpaper,
                KeepNvidiaOverlay = KeepNvidiaOverlay
            };
        }
    }

    internal enum BoostActionOutcome
    {
        Changed,
        Preserved,
        AlreadyOptimal,
        NotFound,
        Skipped,
        Failed,
        Restored,
        ExternalOverridePreserved
    }

    internal sealed class BoostActionRecord
    {
        public DateTime TimestampUtc;
        public string Title;
        public string Detail;
        public BoostActionOutcome Outcome;
    }

    internal sealed class BoostPerformanceResult
    {
        public bool Available;
        public string Error;
        public DateTime CapturedUtc;
        public double AverageFps;
        public double OnePercentLowFps;
        public double P95FrameTimeMs;
        public double P99FrameTimeMs;
        public int Frames;
        public int FramesOver50Ms;
        public int FramesOver100Ms;
        public string ProcessName;
        public string CsvPath;
    }

    internal sealed class BoostSessionReport
    {
        public BoostSessionReport()
        {
            Version = 1;
            Actions = new List<BoostActionRecord>();
        }

        public int Version;
        public string SessionId;
        public string Trigger;
        public string Status;
        public DateTime StartedUtc;
        public DateTime? EndedUtc;
        public long AvailableMemoryStartBytes;
        public long AvailableMemoryEndBytes;
        public int ManagedMemoryMaintenanceCycles;
        public string GameName;
        public string StopReason;
        public List<BoostActionRecord> Actions;
        public BoostPerformanceResult Performance;

        public static BoostSessionReport Start(string trigger)
        {
            return new BoostSessionReport
            {
                SessionId = Guid.NewGuid().ToString("N"),
                Trigger = string.IsNullOrWhiteSpace(trigger) ? "Manual" : trigger,
                Status = "Preparing",
                StartedUtc = DateTime.UtcNow,
                AvailableMemoryStartBytes = BoostSystemMetrics.GetAvailableMemoryBytes()
            };
        }

        public void AddAction(
            string title,
            string detail,
            BoostActionOutcome outcome)
        {
            Actions.Add(new BoostActionRecord
            {
                TimestampUtc = DateTime.UtcNow,
                Title = title ?? string.Empty,
                Detail = detail ?? string.Empty,
                Outcome = outcome
            });
        }

        public void Complete(string status, string reason)
        {
            Status = string.IsNullOrWhiteSpace(status) ? "Completed" : status;
            StopReason = reason ?? string.Empty;
            EndedUtc = DateTime.UtcNow;
            AvailableMemoryEndBytes = BoostSystemMetrics.GetAvailableMemoryBytes();
        }
    }

    internal static class BoostSystemMetrics
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private sealed class MemoryStatusEx
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

        public static long GetAvailableMemoryBytes()
        {
            MemoryStatusEx status;
            if (!TryGetMemoryStatus(out status))
            {
                return 0;
            }
            return status.AvailablePhysical > long.MaxValue
                ? long.MaxValue
                : (long)status.AvailablePhysical;
        }

        public static bool TryGetMemory(
            out long totalBytes,
            out long availableBytes)
        {
            totalBytes = 0;
            availableBytes = 0;
            MemoryStatusEx status;
            if (!TryGetMemoryStatus(out status))
            {
                return false;
            }

            totalBytes = status.TotalPhysical > long.MaxValue
                ? long.MaxValue
                : (long)status.TotalPhysical;
            availableBytes = status.AvailablePhysical > long.MaxValue
                ? long.MaxValue
                : (long)status.AvailablePhysical;
            return true;
        }

        private static bool TryGetMemoryStatus(out MemoryStatusEx status)
        {
            status = new MemoryStatusEx();
            try
            {
                return GlobalMemoryStatusEx(status);
            }
            catch
            {
                return false;
            }
        }
    }

    internal sealed class ActiveMemoryMaintenanceResult
    {
        public bool MemorySnapshotAvailable;
        public bool Collected;
        public long TotalMemoryBytes;
        public long AvailableMemoryBytes;
        public long ManagedHeapBeforeBytes;
        public long ManagedHeapAfterBytes;
    }

    internal static class ActiveMemoryMaintenanceService
    {
        public const int IntervalSeconds = 120;
        public const long MinimumAvailableMemoryBytes = 1024L * 1024L * 1024L;
        public const long ManagedHeapThresholdBytes = 32L * 1024L * 1024L;

        public static long GetNextDueTimestamp(long nowTimestamp)
        {
            long intervalTicks = Stopwatch.Frequency * (long)IntervalSeconds;
            if (nowTimestamp > long.MaxValue - intervalTicks)
            {
                return long.MaxValue;
            }
            return nowTimestamp + intervalTicks;
        }

        public static bool IsDue(long nowTimestamp, long dueTimestamp)
        {
            return dueTimestamp > 0 && nowTimestamp >= dueTimestamp;
        }

        public static long GetAvailableMemoryThreshold(long totalMemoryBytes)
        {
            if (totalMemoryBytes <= 0)
            {
                return MinimumAvailableMemoryBytes;
            }
            return Math.Max(MinimumAvailableMemoryBytes, totalMemoryBytes / 8L);
        }

        public static bool ShouldCollect(
            long totalMemoryBytes,
            long availableMemoryBytes,
            long managedHeapBytes)
        {
            bool systemMemoryPressure =
                totalMemoryBytes > 0 &&
                availableMemoryBytes >= 0 &&
                availableMemoryBytes <= GetAvailableMemoryThreshold(totalMemoryBytes);
            return systemMemoryPressure || managedHeapBytes >= ManagedHeapThresholdBytes;
        }

        public static ActiveMemoryMaintenanceResult Run()
        {
            var result = new ActiveMemoryMaintenanceResult();
            result.ManagedHeapBeforeBytes = GC.GetTotalMemory(false);

            long totalMemoryBytes;
            long availableMemoryBytes;
            result.MemorySnapshotAvailable =
                BoostSystemMetrics.TryGetMemory(out totalMemoryBytes, out availableMemoryBytes);
            result.TotalMemoryBytes = totalMemoryBytes;
            result.AvailableMemoryBytes = availableMemoryBytes;

            if (!ShouldCollect(
                totalMemoryBytes,
                availableMemoryBytes,
                result.ManagedHeapBeforeBytes))
            {
                result.ManagedHeapAfterBytes = result.ManagedHeapBeforeBytes;
                return result;
            }

            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Optimized,
                false);
            result.Collected = true;
            result.ManagedHeapAfterBytes = GC.GetTotalMemory(false);
            return result;
        }
    }

    internal static class BoostPreflightService
    {
        private const int EnumCurrentSettings = -1;

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte AcLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DeviceMode
        {
            private const int CchDeviceName = 32;
            private const int CchFormName = 32;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
            public string DeviceName;
            public short SpecVersion;
            public short DriverVersion;
            public short Size;
            public short DriverExtra;
            public int Fields;
            public int PositionX;
            public int PositionY;
            public int DisplayOrientation;
            public int DisplayFixedOutput;
            public short Color;
            public short Duplex;
            public short YResolution;
            public short TTOption;
            public short Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchFormName)]
            public string FormName;
            public short LogPixels;
            public int BitsPerPel;
            public int PelsWidth;
            public int PelsHeight;
            public int DisplayFlags;
            public int DisplayFrequency;
            public int ICMMethod;
            public int ICMIntent;
            public int MediaType;
            public int DitherType;
            public int Reserved1;
            public int Reserved2;
            public int PanningWidth;
            public int PanningHeight;
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplaySettings(
            string deviceName,
            int modeNumber,
            ref DeviceMode deviceMode);

        public static BoostPreflightReport Run(
            string baseDirectory,
            string optimizationStatus)
        {
            var report = new BoostPreflightReport();
            AddPayloadCheck(report, baseDirectory);
            AddOptimizationCheck(report, optimizationStatus);
            AddRestartCheck(report);
            AddPowerCheck(report);
            AddMemoryCheck(report);
            AddDisplayCheck(report);
            AddStorageCheck(report);
            AddLauncherCheck(report);
            AddGamingSettingsCheck(report);
            AddPowerPlanCheck(report);
            return report;
        }

        private static void AddPayloadCheck(
            BoostPreflightReport report,
            string baseDirectory)
        {
            try
            {
                string[] required =
                {
                    "Game-Boost.ps1",
                    "MaxFPS-Apply.ps1",
                    "MaxFPS-Restore.ps1"
                };
                var missing = new List<string>();
                foreach (string fileName in required)
                {
                    if (!File.Exists(Path.Combine(baseDirectory, fileName)))
                    {
                        missing.Add(fileName);
                    }
                }

                report.Checks.Add(new BoostCheckResult
                {
                    Id = "payload",
                    Title = "КОМПОНЕНТЫ BOOST",
                    Detail = missing.Count == 0
                        ? "Все необходимые компоненты на месте."
                        : "Не найдены: " + string.Join(", ", missing.ToArray()),
                    Severity = missing.Count == 0
                        ? BoostCheckSeverity.Pass
                        : BoostCheckSeverity.Blocked
                });
            }
            catch (Exception ex)
            {
                AddUnknown(report, "payload", "КОМПОНЕНТЫ BOOST", ex);
            }
        }

        private static void AddOptimizationCheck(
            BoostPreflightReport report,
            string status)
        {
            string normalized = status ?? "Unknown";
            BoostCheckSeverity severity;
            string detail;
            if (string.Equals(normalized, "NeedsRecovery", StringComparison.OrdinalIgnoreCase))
            {
                severity = BoostCheckSeverity.Blocked;
                detail = "Сначала завершите безопасное восстановление настроек.";
            }
            else if (string.Equals(normalized, "Active", StringComparison.OrdinalIgnoreCase))
            {
                severity = BoostCheckSeverity.Pass;
                detail = "Системная оптимизация применена, резервная копия доступна.";
            }
            else if (string.Equals(normalized, "NotApplied", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(normalized, "None", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(normalized, "Restored", StringComparison.OrdinalIgnoreCase))
            {
                severity = BoostCheckSeverity.Info;
                detail = "Системная оптимизация ещё не применена.";
            }
            else
            {
                severity = BoostCheckSeverity.Unknown;
                detail = "Состояние системной оптимизации не удалось определить.";
            }

            report.Checks.Add(new BoostCheckResult
            {
                Id = "optimization",
                Title = "СИСТЕМНАЯ ОПТИМИЗАЦИЯ",
                Detail = detail,
                Severity = severity
            });
        }

        private static void AddRestartCheck(BoostPreflightReport report)
        {
            try
            {
                bool pending =
                    RegistryKeyExists(
                        Registry.LocalMachine,
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") ||
                    RegistryKeyExists(
                        Registry.LocalMachine,
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");

                report.Checks.Add(new BoostCheckResult
                {
                    Id = "restart",
                    Title = "ПЕРЕЗАГРУЗКА WINDOWS",
                    Detail = pending
                        ? "Windows ожидает перезагрузку. Перед долгой сессией лучше перезапустить ПК."
                        : "Незавершённая перезагрузка не обнаружена.",
                    Severity = pending
                        ? BoostCheckSeverity.Warning
                        : BoostCheckSeverity.Pass
                });
            }
            catch (Exception ex)
            {
                AddUnknown(report, "restart", "ПЕРЕЗАГРУЗКА WINDOWS", ex);
            }
        }

        private static void AddPowerCheck(BoostPreflightReport report)
        {
            try
            {
                SystemPowerStatus status;
                if (!GetSystemPowerStatus(out status))
                {
                    throw new InvalidOperationException("Windows не вернула состояние питания.");
                }

                bool hasBattery = status.BatteryFlag != 128 && status.BatteryFlag != 255;
                bool onAc = status.AcLineStatus == 1;
                string detail;
                BoostCheckSeverity severity;
                if (!hasBattery)
                {
                    detail = "Стационарное питание обнаружено.";
                    severity = BoostCheckSeverity.Pass;
                }
                else if (onAc)
                {
                    detail = "Ноутбук подключён к питанию.";
                    severity = BoostCheckSeverity.Pass;
                }
                else
                {
                    detail = "Ноутбук работает от батареи — производительность может быть ограничена.";
                    severity = BoostCheckSeverity.Warning;
                }

                report.Checks.Add(new BoostCheckResult
                {
                    Id = "power",
                    Title = "ПИТАНИЕ",
                    Detail = detail,
                    Severity = severity
                });
            }
            catch (Exception ex)
            {
                AddUnknown(report, "power", "ПИТАНИЕ", ex);
            }
        }

        private static void AddMemoryCheck(BoostPreflightReport report)
        {
            try
            {
                long total;
                long available;
                if (!BoostSystemMetrics.TryGetMemory(out total, out available) || total <= 0)
                {
                    throw new InvalidOperationException("Windows не вернула сведения о памяти.");
                }

                report.TotalMemoryBytes = total;
                report.AvailableMemoryBytes = available;
                double availablePercent = available * 100.0 / total;
                bool low = available < 2L * 1024 * 1024 * 1024 || availablePercent < 10;
                report.Checks.Add(new BoostCheckResult
                {
                    Id = "memory",
                    Title = "ОПЕРАТИВНАЯ ПАМЯТЬ",
                    Detail = string.Format(
                        CultureInfo.CurrentCulture,
                        "Доступно {0:0.0} из {1:0.0} ГБ.",
                        available / 1073741824.0,
                        total / 1073741824.0),
                    Severity = low
                        ? BoostCheckSeverity.Warning
                        : BoostCheckSeverity.Pass
                });
            }
            catch (Exception ex)
            {
                AddUnknown(report, "memory", "ОПЕРАТИВНАЯ ПАМЯТЬ", ex);
            }
        }

        private static void AddDisplayCheck(BoostPreflightReport report)
        {
            try
            {
                var mode = new DeviceMode();
                mode.Size = (short)Marshal.SizeOf(typeof(DeviceMode));
                if (!EnumDisplaySettings(null, EnumCurrentSettings, ref mode))
                {
                    throw new InvalidOperationException("Windows не вернула режим дисплея.");
                }

                report.RefreshRate = mode.DisplayFrequency;
                report.Checks.Add(new BoostCheckResult
                {
                    Id = "display",
                    Title = "ЧАСТОТА МОНИТОРА",
                    Detail = mode.DisplayFrequency > 1
                        ? mode.DisplayFrequency.ToString(CultureInfo.CurrentCulture) + " Гц"
                        : "Частота определяется драйвером дисплея.",
                    Severity = mode.DisplayFrequency > 1
                        ? BoostCheckSeverity.Info
                        : BoostCheckSeverity.Unknown
                });
            }
            catch (Exception ex)
            {
                AddUnknown(report, "display", "ЧАСТОТА МОНИТОРА", ex);
            }
        }

        private static void AddStorageCheck(BoostPreflightReport report)
        {
            try
            {
                string systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
                var drive = new DriveInfo(systemRoot);
                long free = drive.AvailableFreeSpace;
                bool low = free < 15L * 1024 * 1024 * 1024;
                report.Checks.Add(new BoostCheckResult
                {
                    Id = "storage",
                    Title = "СВОБОДНОЕ МЕСТО",
                    Detail = string.Format(
                        CultureInfo.CurrentCulture,
                        "На диске {0} свободно {1:0.0} ГБ.",
                        drive.Name,
                        free / 1073741824.0),
                    Severity = low
                        ? BoostCheckSeverity.Warning
                        : BoostCheckSeverity.Pass
                });
            }
            catch (Exception ex)
            {
                AddUnknown(report, "storage", "СВОБОДНОЕ МЕСТО", ex);
            }
        }

        private static void AddLauncherCheck(BoostPreflightReport report)
        {
            try
            {
                string launcherPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MajesticLauncher",
                    "Majestic Launcher.exe");
                bool exists = File.Exists(launcherPath);
                report.Checks.Add(new BoostCheckResult
                {
                    Id = "launcher",
                    Title = "MAJESTIC LAUNCHER",
                    Detail = exists
                        ? "Лаунчер найден."
                        : "Лаунчер не найден в стандартной папке. Boost не сможет запустить его автоматически.",
                    Severity = exists
                        ? BoostCheckSeverity.Pass
                        : BoostCheckSeverity.Warning
                });
            }
            catch (Exception ex)
            {
                AddUnknown(report, "launcher", "MAJESTIC LAUNCHER", ex);
            }
        }

        private static void AddGamingSettingsCheck(BoostPreflightReport report)
        {
            try
            {
                int gameMode = ReadDword(
                    Registry.CurrentUser,
                    @"Software\Microsoft\GameBar",
                    "AutoGameModeEnabled",
                    -1);
                int dvr = ReadDword(
                    Registry.CurrentUser,
                    @"Software\Microsoft\Windows\CurrentVersion\GameDVR",
                    "AppCaptureEnabled",
                    -1);

                bool ready = gameMode == 1 && dvr == 0;
                string detail;
                if (ready)
                {
                    detail = "Game Mode включён, фоновая запись DVR отключена.";
                }
                else if (gameMode == -1 && dvr == -1)
                {
                    detail = "Windows использует настройки игры по умолчанию.";
                }
                else
                {
                    detail = "Игровые параметры Windows отличаются от профиля Boost.";
                }

                report.Checks.Add(new BoostCheckResult
                {
                    Id = "gaming",
                    Title = "ИГРОВЫЕ ПАРАМЕТРЫ WINDOWS",
                    Detail = detail,
                    Severity = ready
                        ? BoostCheckSeverity.Pass
                        : BoostCheckSeverity.Info
                });
            }
            catch (Exception ex)
            {
                AddUnknown(report, "gaming", "ИГРОВЫЕ ПАРАМЕТРЫ WINDOWS", ex);
            }
        }

        private static void AddPowerPlanCheck(BoostPreflightReport report)
        {
            try
            {
                string powerCfg = Path.Combine(Environment.SystemDirectory, "powercfg.exe");
                var startInfo = new ProcessStartInfo
                {
                    FileName = powerCfg,
                    Arguments = "/getactivescheme",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                try
                {
                    Encoding oemEncoding = Encoding.GetEncoding(
                        CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                    startInfo.StandardOutputEncoding = oemEncoding;
                    startInfo.StandardErrorEncoding = oemEncoding;
                }
                catch
                {
                    // The default redirected encoding remains a safe fallback.
                }

                string output;
                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        throw new InvalidOperationException("powercfg не запущен.");
                    }
                    output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(2000))
                    {
                        try { process.Kill(); }
                        catch { }
                        throw new TimeoutException("powercfg не ответил вовремя.");
                    }
                }

                string compact = (output ?? string.Empty).Trim();
                bool maxFps = compact.IndexOf("MAX FPS", StringComparison.OrdinalIgnoreCase) >= 0;
                bool highPerformance =
                    compact.IndexOf("High performance", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    compact.IndexOf("Высокая производительность", StringComparison.OrdinalIgnoreCase) >= 0;
                report.Checks.Add(new BoostCheckResult
                {
                    Id = "power-plan",
                    Title = "ПЛАН ПИТАНИЯ",
                    Detail = string.IsNullOrWhiteSpace(compact)
                        ? "Активный план питания не удалось прочитать."
                        : compact,
                    Severity = maxFps || highPerformance
                        ? BoostCheckSeverity.Pass
                        : BoostCheckSeverity.Info
                });
            }
            catch (Exception ex)
            {
                AddUnknown(report, "power-plan", "ПЛАН ПИТАНИЯ", ex);
            }
        }

        private static bool RegistryKeyExists(
            RegistryKey root,
            string path)
        {
            using (RegistryKey key = root.OpenSubKey(path, false))
            {
                return key != null;
            }
        }

        private static int ReadDword(
            RegistryKey root,
            string path,
            string name,
            int fallback)
        {
            using (RegistryKey key = root.OpenSubKey(path, false))
            {
                if (key == null)
                {
                    return fallback;
                }
                object value = key.GetValue(name, fallback);
                return value is int ? (int)value : fallback;
            }
        }

        private static void AddUnknown(
            BoostPreflightReport report,
            string id,
            string title,
            Exception error)
        {
            report.Checks.Add(new BoostCheckResult
            {
                Id = id,
                Title = title,
                Detail = "Проверка недоступна: " + error.Message,
                Severity = BoostCheckSeverity.Unknown
            });
        }
    }

    internal static class BoostSessionReportStore
    {
        private const int MaxReports = 20;
        private const int MaxReportBytes = 1024 * 1024;

        public static string StateDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MajesticBoost");
            }
        }

        public static string SessionsDirectory
        {
            get { return Path.Combine(StateDirectory, "Sessions"); }
        }

        public static void Save(BoostSessionReport report)
        {
            if (report == null || string.IsNullOrWhiteSpace(report.SessionId))
            {
                return;
            }

            Directory.CreateDirectory(SessionsDirectory);
            string content = Serialize(report);
            string sessionPath = Path.Combine(
                SessionsDirectory,
                "session-" + report.SessionId + ".report");
            WriteAllTextAtomic(sessionPath, content);
            WriteAllTextAtomic(Path.Combine(StateDirectory, "last-session.report"), content);
            PruneOldReports();
        }

        public static BoostSessionReport LoadLast()
        {
            string path = Path.Combine(StateDirectory, "last-session.report");
            return Load(path);
        }

        public static BoostSessionReport Load(string path)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length <= 0 || file.Length > MaxReportBytes)
                {
                    return null;
                }
                return Deserialize(File.ReadAllLines(path, Encoding.UTF8));
            }
            catch
            {
                return null;
            }
        }

        private static string Serialize(BoostSessionReport report)
        {
            var lines = new List<string>();
            lines.Add("Version=" + report.Version.ToString(CultureInfo.InvariantCulture));
            lines.Add("SessionId=" + Encode(report.SessionId));
            lines.Add("Trigger=" + Encode(report.Trigger));
            lines.Add("Status=" + Encode(report.Status));
            lines.Add("StartedUtc=" + report.StartedUtc.ToString("o", CultureInfo.InvariantCulture));
            lines.Add("EndedUtc=" + (report.EndedUtc.HasValue
                ? report.EndedUtc.Value.ToString("o", CultureInfo.InvariantCulture)
                : string.Empty));
            lines.Add("AvailableMemoryStartBytes=" +
                report.AvailableMemoryStartBytes.ToString(CultureInfo.InvariantCulture));
            lines.Add("AvailableMemoryEndBytes=" +
                report.AvailableMemoryEndBytes.ToString(CultureInfo.InvariantCulture));
            lines.Add("ManagedMemoryMaintenanceCycles=" +
                report.ManagedMemoryMaintenanceCycles.ToString(CultureInfo.InvariantCulture));
            lines.Add("GameName=" + Encode(report.GameName));
            lines.Add("StopReason=" + Encode(report.StopReason));

            if (report.Performance != null)
            {
                lines.Add("PerformanceAvailable=" + report.Performance.Available);
                lines.Add("PerformanceError=" + Encode(report.Performance.Error));
                lines.Add("PerformanceCapturedUtc=" +
                    report.Performance.CapturedUtc.ToString("o", CultureInfo.InvariantCulture));
                lines.Add("AverageFps=" +
                    report.Performance.AverageFps.ToString("R", CultureInfo.InvariantCulture));
                lines.Add("OnePercentLowFps=" +
                    report.Performance.OnePercentLowFps.ToString("R", CultureInfo.InvariantCulture));
                lines.Add("P95FrameTimeMs=" +
                    report.Performance.P95FrameTimeMs.ToString("R", CultureInfo.InvariantCulture));
                lines.Add("P99FrameTimeMs=" +
                    report.Performance.P99FrameTimeMs.ToString("R", CultureInfo.InvariantCulture));
                lines.Add("Frames=" +
                    report.Performance.Frames.ToString(CultureInfo.InvariantCulture));
                lines.Add("FramesOver50Ms=" +
                    report.Performance.FramesOver50Ms.ToString(CultureInfo.InvariantCulture));
                lines.Add("FramesOver100Ms=" +
                    report.Performance.FramesOver100Ms.ToString(CultureInfo.InvariantCulture));
                lines.Add("PerformanceProcess=" + Encode(report.Performance.ProcessName));
                lines.Add("PerformanceCsvPath=" + Encode(report.Performance.CsvPath));
            }

            foreach (BoostActionRecord action in report.Actions ?? new List<BoostActionRecord>())
            {
                string payload = string.Join(
                    "\t",
                    new[]
                    {
                        action.TimestampUtc.ToString("o", CultureInfo.InvariantCulture),
                        action.Outcome.ToString(),
                        action.Title ?? string.Empty,
                        action.Detail ?? string.Empty
                    });
                lines.Add("Action=" + Encode(payload));
            }
            return string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine;
        }

        private static BoostSessionReport Deserialize(string[] lines)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var actionValues = new List<string>();
            foreach (string line in lines)
            {
                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }
                string key = line.Substring(0, separator);
                string value = line.Substring(separator + 1);
                if (string.Equals(key, "Action", StringComparison.OrdinalIgnoreCase))
                {
                    actionValues.Add(value);
                }
                else
                {
                    values[key] = value;
                }
            }

            int version;
            if (!TryParseInt(values, "Version", out version) || version != 1)
            {
                return null;
            }

            DateTime startedUtc;
            if (!TryParseDate(values, "StartedUtc", out startedUtc))
            {
                return null;
            }

            var report = new BoostSessionReport
            {
                Version = version,
                SessionId = Decode(GetValue(values, "SessionId")),
                Trigger = Decode(GetValue(values, "Trigger")),
                Status = Decode(GetValue(values, "Status")),
                StartedUtc = startedUtc,
                GameName = Decode(GetValue(values, "GameName")),
                StopReason = Decode(GetValue(values, "StopReason"))
            };
            long longValue;
            if (TryParseLong(values, "AvailableMemoryStartBytes", out longValue))
            {
                report.AvailableMemoryStartBytes = longValue;
            }
            if (TryParseLong(values, "AvailableMemoryEndBytes", out longValue))
            {
                report.AvailableMemoryEndBytes = longValue;
            }
            int memoryMaintenanceCycles;
            if (TryParseInt(values, "ManagedMemoryMaintenanceCycles", out memoryMaintenanceCycles))
            {
                report.ManagedMemoryMaintenanceCycles = Math.Max(0, memoryMaintenanceCycles);
            }
            DateTime dateValue;
            if (TryParseDate(values, "EndedUtc", out dateValue))
            {
                report.EndedUtc = dateValue;
            }

            bool performanceAvailable;
            if (bool.TryParse(GetValue(values, "PerformanceAvailable"), out performanceAvailable))
            {
                report.Performance = new BoostPerformanceResult
                {
                    Available = performanceAvailable,
                    Error = Decode(GetValue(values, "PerformanceError")),
                    ProcessName = Decode(GetValue(values, "PerformanceProcess")),
                    CsvPath = Decode(GetValue(values, "PerformanceCsvPath"))
                };
                if (TryParseDate(values, "PerformanceCapturedUtc", out dateValue))
                {
                    report.Performance.CapturedUtc = dateValue;
                }
                report.Performance.AverageFps = ParseDouble(values, "AverageFps");
                report.Performance.OnePercentLowFps = ParseDouble(values, "OnePercentLowFps");
                report.Performance.P95FrameTimeMs = ParseDouble(values, "P95FrameTimeMs");
                report.Performance.P99FrameTimeMs = ParseDouble(values, "P99FrameTimeMs");
                int intValue;
                if (TryParseInt(values, "Frames", out intValue))
                {
                    report.Performance.Frames = intValue;
                }
                if (TryParseInt(values, "FramesOver50Ms", out intValue))
                {
                    report.Performance.FramesOver50Ms = intValue;
                }
                if (TryParseInt(values, "FramesOver100Ms", out intValue))
                {
                    report.Performance.FramesOver100Ms = intValue;
                }
            }

            foreach (string encoded in actionValues)
            {
                string payload = Decode(encoded);
                string[] parts = payload.Split(new[] { '\t' }, 4);
                if (parts.Length != 4)
                {
                    continue;
                }
                DateTime timestamp;
                BoostActionOutcome outcome;
                if (!DateTime.TryParse(
                        parts[0],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out timestamp) ||
                    !Enum.TryParse(parts[1], true, out outcome))
                {
                    continue;
                }
                report.Actions.Add(new BoostActionRecord
                {
                    TimestampUtc = timestamp,
                    Outcome = outcome,
                    Title = parts[2],
                    Detail = parts[3]
                });
            }
            return report;
        }

        private static void WriteAllTextAtomic(string destination, string content)
        {
            string directory = Path.GetDirectoryName(destination);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(
                directory,
                "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            string backup = temporary + ".bak";
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(content ?? string.Empty);
                using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(destination))
                {
                    File.Replace(temporary, destination, backup, true);
                }
                else
                {
                    File.Move(temporary, destination);
                }
            }
            finally
            {
                TryDelete(temporary);
                TryDelete(backup);
            }
        }

        private static void PruneOldReports()
        {
            try
            {
                var directory = new DirectoryInfo(SessionsDirectory);
                FileInfo[] reports = directory.GetFiles("session-*.report")
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToArray();
                for (int index = MaxReports; index < reports.Length; index++)
                {
                    string fullPath = Path.GetFullPath(reports[index].FullName);
                    string requiredPrefix = Path.GetFullPath(SessionsDirectory)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                        Path.DirectorySeparatorChar;
                    if (fullPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(fullPath);
                    }
                }
            }
            catch { }
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(
                Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            try
            {
                if (string.IsNullOrEmpty(value))
                {
                    return string.Empty;
                }
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetValue(
            IDictionary<string, string> values,
            string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static bool TryParseInt(
            IDictionary<string, string> values,
            string key,
            out int value)
        {
            return int.TryParse(
                GetValue(values, key),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static bool TryParseLong(
            IDictionary<string, string> values,
            string key,
            out long value)
        {
            return long.TryParse(
                GetValue(values, key),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static bool TryParseDate(
            IDictionary<string, string> values,
            string key,
            out DateTime value)
        {
            return DateTime.TryParse(
                GetValue(values, key),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out value);
        }

        private static double ParseDouble(
            IDictionary<string, string> values,
            string key)
        {
            double value;
            return double.TryParse(
                GetValue(values, key),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value)
                ? value
                : 0;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch { }
        }
    }
}
