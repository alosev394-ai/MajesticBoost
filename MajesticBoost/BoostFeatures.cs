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
        public long CommitTotalBytes;
        public long CommitLimitBytes;
        public long CommitHeadroomBytes;
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
            Version = 2;
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
        public int MemorySamples;
        public int MemoryReliefAttempts;
        public int MemoryReliefSuccesses;
        public long MemoryReliefBytes;
        public long MinimumAvailableMemoryBytes;
        public long MinimumCommitHeadroomBytes;
        public long PeakGameWorkingSetBytes;
        public long PeakGamePrivateBytes;
        public string GameCrashCode;
        public string GameCrashModule;
        public string GameCrashOffset;
        public DateTime? GameCrashUtc;
        public string GameName;
        public string StopReason;
        public List<BoostActionRecord> Actions;
        public BoostPerformanceResult Performance;

        public static BoostSessionReport Start(string trigger)
        {
            var report = new BoostSessionReport
            {
                SessionId = Guid.NewGuid().ToString("N"),
                Trigger = string.IsNullOrWhiteSpace(trigger) ? "Manual" : trigger,
                Status = "Preparing",
                StartedUtc = DateTime.UtcNow
            };
            MemoryPressureSnapshot snapshot;
            if (BoostSystemMetrics.TryGetPerformanceSnapshot(out snapshot))
            {
                report.AvailableMemoryStartBytes = snapshot.AvailablePhysicalBytes;
                report.MinimumAvailableMemoryBytes = snapshot.AvailablePhysicalBytes;
                report.MinimumCommitHeadroomBytes = snapshot.CommitHeadroomBytes;
                report.MemorySamples = 1;
            }
            else
            {
                report.AvailableMemoryStartBytes = BoostSystemMetrics.GetAvailableMemoryBytes();
                report.MinimumAvailableMemoryBytes = report.AvailableMemoryStartBytes;
            }
            return report;
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
            MemoryPressureSnapshot snapshot;
            if (BoostSystemMetrics.TryGetPerformanceSnapshot(out snapshot))
            {
                AvailableMemoryEndBytes = snapshot.AvailablePhysicalBytes;
                MinimumAvailableMemoryBytes = MinimumPositive(
                    MinimumAvailableMemoryBytes,
                    snapshot.AvailablePhysicalBytes);
                MinimumCommitHeadroomBytes = MinimumPositive(
                    MinimumCommitHeadroomBytes,
                    snapshot.CommitHeadroomBytes);
                MemorySamples++;
            }
            else
            {
                AvailableMemoryEndBytes = BoostSystemMetrics.GetAvailableMemoryBytes();
            }
        }

        private static long MinimumPositive(long current, long candidate)
        {
            if (candidate <= 0)
            {
                return current;
            }
            return current <= 0 ? candidate : Math.Min(current, candidate);
        }
    }

    internal static class BoostSystemMetrics
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PerformanceInformationNative
        {
            public uint Size;
            public UIntPtr CommitTotal;
            public UIntPtr CommitLimit;
            public UIntPtr CommitPeak;
            public UIntPtr PhysicalTotal;
            public UIntPtr PhysicalAvailable;
            public UIntPtr SystemCache;
            public UIntPtr KernelTotal;
            public UIntPtr KernelPaged;
            public UIntPtr KernelNonpaged;
            public UIntPtr PageSize;
            public uint HandleCount;
            public uint ProcessCount;
            public uint ThreadCount;
        }

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

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPerformanceInfo(
            out PerformanceInformationNative information,
            uint size);

        public static long GetAvailableMemoryBytes()
        {
            MemoryPressureSnapshot snapshot;
            if (TryGetPerformanceSnapshot(out snapshot))
            {
                return snapshot.AvailablePhysicalBytes;
            }

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
            MemoryPressureSnapshot snapshot;
            if (TryGetPerformanceSnapshot(out snapshot))
            {
                totalBytes = snapshot.TotalPhysicalBytes;
                availableBytes = snapshot.AvailablePhysicalBytes;
                return true;
            }

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

        public static bool TryGetPerformanceSnapshot(
            out MemoryPressureSnapshot snapshot)
        {
            snapshot = new MemoryPressureSnapshot
            {
                CapturedUtc = DateTime.UtcNow
            };

            PerformanceInformationNative information;
            uint size = (uint)Marshal.SizeOf(typeof(PerformanceInformationNative));
            try
            {
                if (!GetPerformanceInfo(out information, size))
                {
                    snapshot.ErrorCode = Marshal.GetLastWin32Error();
                    return false;
                }
            }
            catch
            {
                snapshot.ErrorCode = Marshal.GetLastWin32Error();
                return false;
            }

            long pageSize = ConvertPagesToBytes(new UIntPtr(1), information.PageSize);
            snapshot.PageSizeBytes = pageSize;
            snapshot.TotalPhysicalBytes =
                ConvertPagesToBytes(information.PhysicalTotal, information.PageSize);

            // Windows defines PhysicalAvailable as standby + free + zero pages.
            // Those pages can be reused immediately and must not be treated as
            // blocked memory that needs a standby-list purge.
            snapshot.AvailablePhysicalBytes =
                ConvertPagesToBytes(information.PhysicalAvailable, information.PageSize);
            snapshot.SystemCacheBytes =
                ConvertPagesToBytes(information.SystemCache, information.PageSize);
            snapshot.CommitTotalBytes =
                ConvertPagesToBytes(information.CommitTotal, information.PageSize);
            snapshot.CommitLimitBytes =
                ConvertPagesToBytes(information.CommitLimit, information.PageSize);
            snapshot.CommitHeadroomBytes = Math.Max(
                0,
                snapshot.CommitLimitBytes - snapshot.CommitTotalBytes);
            snapshot.MetricsAvailable =
                pageSize > 0 &&
                snapshot.TotalPhysicalBytes > 0 &&
                snapshot.CommitLimitBytes > 0;

            long workingSetBytes;
            long privateBytes;
            snapshot.ProcessMetricsAvailable = TryGetCurrentProcessMemory(
                out workingSetBytes,
                out privateBytes);
            snapshot.CurrentProcessWorkingSetBytes = workingSetBytes;
            snapshot.CurrentProcessPrivateBytes = privateBytes;
            return snapshot.MetricsAvailable;
        }

        private static bool TryGetCurrentProcessMemory(
            out long workingSetBytes,
            out long privateBytes)
        {
            workingSetBytes = 0;
            privateBytes = 0;
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    process.Refresh();
                    workingSetBytes = Math.Max(0, process.WorkingSet64);
                    privateBytes = Math.Max(0, process.PrivateMemorySize64);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static long ConvertPagesToBytes(UIntPtr pages, UIntPtr pageSize)
        {
            ulong pageCount = pages.ToUInt64();
            ulong bytesPerPage = pageSize.ToUInt64();
            if (pageCount == 0 || bytesPerPage == 0)
            {
                return 0;
            }
            if (pageCount > (ulong)long.MaxValue / bytesPerPage)
            {
                return long.MaxValue;
            }
            return (long)(pageCount * bytesPerPage);
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

    internal sealed class MemoryPressureSnapshot
    {
        public DateTime CapturedUtc;
        public bool MetricsAvailable;
        public bool ProcessMetricsAvailable;
        public int ErrorCode;
        public long PageSizeBytes;
        public long TotalPhysicalBytes;
        public long AvailablePhysicalBytes;
        public long SystemCacheBytes;
        public long CommitTotalBytes;
        public long CommitLimitBytes;
        public long CommitHeadroomBytes;
        public long CurrentProcessWorkingSetBytes;
        public long CurrentProcessPrivateBytes;
    }

    internal enum MemoryPressureReliefDecision
    {
        MetricsUnavailable,
        NoPressure,
        AwaitingSecondSample,
        Cooldown,
        AttemptLimitReached,
        ReliefRequired
    }

    internal enum MemoryPressureReliefStatus
    {
        MetricsUnavailable,
        NoPressure,
        AwaitingSecondSample,
        Cooldown,
        AttemptLimitReached,
        Failed,
        NoEffect,
        Succeeded
    }

    internal sealed class MemoryPressureReliefPolicyState
    {
        public int ConsecutiveCriticalSamples;
        public int Attempts;
        public long NextAllowedTimestamp;

        public MemoryPressureReliefPolicyState Clone()
        {
            return new MemoryPressureReliefPolicyState
            {
                ConsecutiveCriticalSamples = ConsecutiveCriticalSamples,
                Attempts = Attempts,
                NextAllowedTimestamp = NextAllowedTimestamp
            };
        }
    }

    internal sealed class MemoryPressureReliefEvaluation
    {
        public MemoryPressureReliefDecision Decision;
        public string Reason;
        public MemoryPressureReliefPolicyState NextState;
    }

    internal static class MemoryPressureReliefPolicy
    {
        public const int RequiredCriticalSamples = 2;
        public const int CooldownSeconds = 600;
        public const int NoEffectBackoffSeconds = 1800;
        public const int MaximumAttempts = 3;
        public const long MinimumPhysicalThresholdBytes = 512L * 1024L * 1024L;
        public const long MaximumPhysicalThresholdBytes = 1536L * 1024L * 1024L;
        public const long MinimumCommitThresholdBytes = 512L * 1024L * 1024L;
        public const long MaximumCommitThresholdBytes = 2L * 1024L * 1024L * 1024L;

        public static long GetPhysicalCriticalThreshold(long totalPhysicalBytes)
        {
            return Clamp(
                totalPhysicalBytes > 0 ? totalPhysicalBytes / 20L : 0,
                MinimumPhysicalThresholdBytes,
                MaximumPhysicalThresholdBytes);
        }

        public static long GetCommitCriticalThreshold(long commitLimitBytes)
        {
            return Clamp(
                commitLimitBytes > 0 ? commitLimitBytes / 20L : 0,
                MinimumCommitThresholdBytes,
                MaximumCommitThresholdBytes);
        }

        public static bool IsCritical(MemoryPressureSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.MetricsAvailable)
            {
                return false;
            }

            bool physicalPressure =
                snapshot.TotalPhysicalBytes > 0 &&
                snapshot.AvailablePhysicalBytes >= 0 &&
                snapshot.AvailablePhysicalBytes <=
                    GetPhysicalCriticalThreshold(snapshot.TotalPhysicalBytes);
            bool commitPressure =
                snapshot.CommitLimitBytes > 0 &&
                snapshot.CommitHeadroomBytes >= 0 &&
                snapshot.CommitHeadroomBytes <=
                    GetCommitCriticalThreshold(snapshot.CommitLimitBytes);
            return physicalPressure || commitPressure;
        }

        public static MemoryPressureReliefEvaluation Evaluate(
            MemoryPressureSnapshot snapshot,
            MemoryPressureReliefPolicyState state,
            long nowTimestamp)
        {
            var next = state == null
                ? new MemoryPressureReliefPolicyState()
                : state.Clone();

            if (snapshot == null || !snapshot.MetricsAvailable)
            {
                next.ConsecutiveCriticalSamples = 0;
                return NewEvaluation(
                    MemoryPressureReliefDecision.MetricsUnavailable,
                    "Windows memory metrics are unavailable.",
                    next);
            }

            if (!IsCritical(snapshot))
            {
                next.ConsecutiveCriticalSamples = 0;
                return NewEvaluation(
                    MemoryPressureReliefDecision.NoPressure,
                    "Available physical memory and commit headroom are healthy.",
                    next);
            }

            if (next.ConsecutiveCriticalSamples < int.MaxValue)
            {
                next.ConsecutiveCriticalSamples++;
            }

            if (next.Attempts >= MaximumAttempts)
            {
                return NewEvaluation(
                    MemoryPressureReliefDecision.AttemptLimitReached,
                    "The per-session memory-relief attempt limit was reached.",
                    next);
            }

            if (next.NextAllowedTimestamp > 0 &&
                nowTimestamp < next.NextAllowedTimestamp)
            {
                return NewEvaluation(
                    MemoryPressureReliefDecision.Cooldown,
                    "Memory relief is in cooldown.",
                    next);
            }

            if (next.ConsecutiveCriticalSamples < RequiredCriticalSamples)
            {
                return NewEvaluation(
                    MemoryPressureReliefDecision.AwaitingSecondSample,
                    "Critical pressure must be confirmed by a second sample.",
                    next);
            }

            return NewEvaluation(
                MemoryPressureReliefDecision.ReliefRequired,
                "Critical physical or commit pressure was confirmed.",
                next);
        }

        public static MemoryPressureReliefPolicyState RecordAttempt(
            MemoryPressureReliefPolicyState state,
            long nowTimestamp,
            long reclaimedWorkingSetBytes)
        {
            var next = state == null
                ? new MemoryPressureReliefPolicyState()
                : state.Clone();
            if (next.Attempts < int.MaxValue)
            {
                next.Attempts++;
            }
            next.ConsecutiveCriticalSamples = 0;
            next.NextAllowedTimestamp = AddSecondsSaturated(
                nowTimestamp,
                reclaimedWorkingSetBytes > 0
                    ? CooldownSeconds
                    : NoEffectBackoffSeconds);
            return next;
        }

        private static MemoryPressureReliefEvaluation NewEvaluation(
            MemoryPressureReliefDecision decision,
            string reason,
            MemoryPressureReliefPolicyState state)
        {
            return new MemoryPressureReliefEvaluation
            {
                Decision = decision,
                Reason = reason,
                NextState = state
            };
        }

        private static long AddSecondsSaturated(long timestamp, int seconds)
        {
            long ticks = Stopwatch.Frequency * (long)seconds;
            return timestamp > long.MaxValue - ticks
                ? long.MaxValue
                : timestamp + ticks;
        }

        private static long Clamp(long value, long minimum, long maximum)
        {
            return Math.Min(maximum, Math.Max(minimum, value));
        }
    }

    internal sealed class ActiveMemoryMaintenanceResult
    {
        public bool MemorySnapshotAvailable;
        public bool Collected;
        public bool Attempted;
        public bool Success;
        public bool NativeCallSucceeded;
        public int NativeErrorCode;
        public string Reason;
        public MemoryPressureReliefStatus Status;
        public MemoryPressureSnapshot Before;
        public MemoryPressureSnapshot After;
        public long TotalMemoryBytes;
        public long AvailableMemoryBytes;
        public long ManagedHeapBeforeBytes;
        public long ManagedHeapAfterBytes;
        public long ReclaimedManagedHeapBytes;
        public long ReclaimedWorkingSetBytes;
        public long ReclaimedPrivateBytes;
        public long AvailablePhysicalDeltaBytes;
        public long CommitHeadroomDeltaBytes;
        public long DurationMilliseconds;
    }

    internal static class ActiveMemoryMaintenanceService
    {
        public const int IntervalSeconds = 60;
        public const long MinimumAvailableMemoryBytes = 1024L * 1024L * 1024L;
        public const long ManagedHeapThresholdBytes = 32L * 1024L * 1024L;
        private static readonly object ReliefSync = new object();
        private static MemoryPressureReliefPolicyState defaultPolicyState =
            new MemoryPressureReliefPolicyState();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyWorkingSet(IntPtr process);

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
            lock (ReliefSync)
            {
                MemoryPressureSnapshot snapshot;
                bool snapshotAvailable =
                    BoostSystemMetrics.TryGetPerformanceSnapshot(out snapshot);
                long nowTimestamp = Stopwatch.GetTimestamp();
                MemoryPressureReliefEvaluation evaluation =
                    MemoryPressureReliefPolicy.Evaluate(
                        snapshot,
                        defaultPolicyState,
                        nowTimestamp);
                defaultPolicyState = evaluation.NextState;

                if (evaluation.Decision != MemoryPressureReliefDecision.ReliefRequired)
                {
                    return NewSkippedResult(
                        snapshot,
                        snapshotAvailable,
                        evaluation.Decision,
                        evaluation.Reason);
                }

                ActiveMemoryMaintenanceResult result =
                    RunImmediateForCurrentProcess(evaluation.Reason);
                defaultPolicyState = MemoryPressureReliefPolicy.RecordAttempt(
                    defaultPolicyState,
                    nowTimestamp,
                    result.ReclaimedWorkingSetBytes);
                return result;
            }
        }

        public static ActiveMemoryMaintenanceResult RunImmediateForCurrentProcess(
            string reason)
        {
            var result = new ActiveMemoryMaintenanceResult
            {
                Attempted = true,
                Reason = reason ?? string.Empty
            };
            long startedTimestamp = Stopwatch.GetTimestamp();
            result.MemorySnapshotAvailable =
                BoostSystemMetrics.TryGetPerformanceSnapshot(out result.Before);
            PopulateLegacySnapshotFields(result);
            result.ManagedHeapBeforeBytes = GC.GetTotalMemory(false);
            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                true,
                false);
            result.ManagedHeapAfterBytes = GC.GetTotalMemory(false);
            result.ReclaimedManagedHeapBytes = Math.Max(
                0,
                result.ManagedHeapBeforeBytes - result.ManagedHeapAfterBytes);

            int errorCode;
            bool nativeSucceeded = TryTrimCurrentProcessWorkingSet(out errorCode);
            result.NativeCallSucceeded = nativeSucceeded;
            result.NativeErrorCode = errorCode;
            bool afterAvailable =
                BoostSystemMetrics.TryGetPerformanceSnapshot(out result.After);
            result.ReclaimedWorkingSetBytes = Reclaimed(
                result.Before != null && result.Before.ProcessMetricsAvailable
                    ? result.Before.CurrentProcessWorkingSetBytes
                    : 0,
                result.After != null && result.After.ProcessMetricsAvailable
                    ? result.After.CurrentProcessWorkingSetBytes
                    : 0);
            result.ReclaimedPrivateBytes = Reclaimed(
                result.Before != null && result.Before.ProcessMetricsAvailable
                    ? result.Before.CurrentProcessPrivateBytes
                    : 0,
                result.After != null && result.After.ProcessMetricsAvailable
                    ? result.After.CurrentProcessPrivateBytes
                    : 0);
            if (result.Before != null && result.After != null &&
                result.Before.MetricsAvailable && result.After.MetricsAvailable)
            {
                result.AvailablePhysicalDeltaBytes =
                    result.After.AvailablePhysicalBytes -
                    result.Before.AvailablePhysicalBytes;
                result.CommitHeadroomDeltaBytes =
                    result.After.CommitHeadroomBytes -
                    result.Before.CommitHeadroomBytes;
            }

            result.Status = ClassifyReliefOutcome(
                nativeSucceeded,
                afterAvailable &&
                    result.Before != null &&
                    result.Before.ProcessMetricsAvailable &&
                    result.After != null &&
                    result.After.ProcessMetricsAvailable,
                result.ReclaimedWorkingSetBytes);
            result.Success = result.Status == MemoryPressureReliefStatus.Succeeded;
            // Kept for Program.cs compatibility: a cycle is counted only when
            // a measurable amount of this process's working set was reclaimed.
            result.Collected = result.Success;
            string triggerReason = result.Reason;
            if (result.Status == MemoryPressureReliefStatus.Failed)
            {
                result.Reason =
                    "EmptyWorkingSet failed for the current process (Win32 " +
                    result.NativeErrorCode.ToString(CultureInfo.InvariantCulture) +
                    "). Trigger: " + triggerReason;
            }
            else if (result.Status == MemoryPressureReliefStatus.MetricsUnavailable)
            {
                result.Reason =
                    "The current-process working-set result could not be measured. Trigger: " +
                    triggerReason;
            }
            else if (result.Status == MemoryPressureReliefStatus.NoEffect)
            {
                result.Reason =
                    "The current process yielded no measurable working-set bytes. Trigger: " +
                    triggerReason;
            }
            else
            {
                result.Reason =
                    "The current process yielded " +
                    result.ReclaimedWorkingSetBytes.ToString(CultureInfo.InvariantCulture) +
                    " working-set bytes. Trigger: " + triggerReason;
            }
            result.DurationMilliseconds = ElapsedMilliseconds(
                startedTimestamp,
                Stopwatch.GetTimestamp());
            return result;
        }

        public static MemoryPressureReliefStatus ClassifyReliefOutcome(
            bool nativeSucceeded,
            bool afterMetricsAvailable,
            long reclaimedWorkingSetBytes)
        {
            if (!nativeSucceeded)
            {
                return MemoryPressureReliefStatus.Failed;
            }
            if (!afterMetricsAvailable)
            {
                return MemoryPressureReliefStatus.MetricsUnavailable;
            }
            return reclaimedWorkingSetBytes > 0
                ? MemoryPressureReliefStatus.Succeeded
                : MemoryPressureReliefStatus.NoEffect;
        }

        public static void ResetPolicyState()
        {
            lock (ReliefSync)
            {
                defaultPolicyState = new MemoryPressureReliefPolicyState();
            }
        }

        private static bool TryTrimCurrentProcessWorkingSet(out int errorCode)
        {
            errorCode = 0;
            try
            {
                if (EmptyWorkingSet(GetCurrentProcess()))
                {
                    return true;
                }
                errorCode = Marshal.GetLastWin32Error();
                return false;
            }
            catch
            {
                errorCode = Marshal.GetLastWin32Error();
                return false;
            }
        }

        private static ActiveMemoryMaintenanceResult NewSkippedResult(
            MemoryPressureSnapshot snapshot,
            bool snapshotAvailable,
            MemoryPressureReliefDecision decision,
            string reason)
        {
            var result = new ActiveMemoryMaintenanceResult
            {
                Before = snapshot,
                After = snapshot,
                MemorySnapshotAvailable = snapshotAvailable,
                Reason = reason ?? string.Empty,
                Status = MapStatus(decision)
            };
            PopulateLegacySnapshotFields(result);
            result.ManagedHeapBeforeBytes = GC.GetTotalMemory(false);
            result.ManagedHeapAfterBytes = result.ManagedHeapBeforeBytes;
            return result;
        }

        private static void PopulateLegacySnapshotFields(
            ActiveMemoryMaintenanceResult result)
        {
            if (result.Before == null)
            {
                return;
            }
            result.TotalMemoryBytes = result.Before.TotalPhysicalBytes;
            result.AvailableMemoryBytes = result.Before.AvailablePhysicalBytes;
        }

        private static MemoryPressureReliefStatus MapStatus(
            MemoryPressureReliefDecision decision)
        {
            switch (decision)
            {
                case MemoryPressureReliefDecision.NoPressure:
                    return MemoryPressureReliefStatus.NoPressure;
                case MemoryPressureReliefDecision.AwaitingSecondSample:
                    return MemoryPressureReliefStatus.AwaitingSecondSample;
                case MemoryPressureReliefDecision.Cooldown:
                    return MemoryPressureReliefStatus.Cooldown;
                case MemoryPressureReliefDecision.AttemptLimitReached:
                    return MemoryPressureReliefStatus.AttemptLimitReached;
                default:
                    return MemoryPressureReliefStatus.MetricsUnavailable;
            }
        }

        private static long Reclaimed(long beforeBytes, long afterBytes)
        {
            return beforeBytes > afterBytes
                ? beforeBytes - afterBytes
                : 0;
        }

        private static long ElapsedMilliseconds(
            long startedTimestamp,
            long finishedTimestamp)
        {
            if (finishedTimestamp <= startedTimestamp || Stopwatch.Frequency <= 0)
            {
                return 0;
            }
            double milliseconds =
                (finishedTimestamp - startedTimestamp) * 1000.0 /
                Stopwatch.Frequency;
            return milliseconds >= long.MaxValue
                ? long.MaxValue
                : (long)Math.Max(0, milliseconds);
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
                MemoryPressureSnapshot snapshot;
                if (!BoostSystemMetrics.TryGetPerformanceSnapshot(out snapshot) ||
                    snapshot.TotalPhysicalBytes <= 0)
                {
                    throw new InvalidOperationException("Windows не вернула сведения о памяти.");
                }

                report.TotalMemoryBytes = snapshot.TotalPhysicalBytes;
                report.AvailableMemoryBytes = snapshot.AvailablePhysicalBytes;
                report.CommitTotalBytes = snapshot.CommitTotalBytes;
                report.CommitLimitBytes = snapshot.CommitLimitBytes;
                report.CommitHeadroomBytes = snapshot.CommitHeadroomBytes;
                double availablePercent =
                    snapshot.AvailablePhysicalBytes * 100.0 /
                    snapshot.TotalPhysicalBytes;
                double commitHeadroomPercent =
                    snapshot.CommitLimitBytes > 0
                        ? snapshot.CommitHeadroomBytes * 100.0 /
                          snapshot.CommitLimitBytes
                        : 0;
                bool low =
                    snapshot.AvailablePhysicalBytes < 2L * 1024 * 1024 * 1024 ||
                    availablePercent < 10 ||
                    snapshot.CommitHeadroomBytes < 2L * 1024 * 1024 * 1024 ||
                    commitHeadroomPercent < 10;
                report.Checks.Add(new BoostCheckResult
                {
                    Id = "memory",
                    Title = "ОПЕРАТИВНАЯ ПАМЯТЬ",
                    Detail = string.Format(
                        CultureInfo.CurrentCulture,
                        "Доступно {0:0.0} из {1:0.0} ГБ (кэш Windows уже учтён). Commit {2:0.0} из {3:0.0} ГБ, запас {4:0.0} ГБ.",
                        snapshot.AvailablePhysicalBytes / 1073741824.0,
                        snapshot.TotalPhysicalBytes / 1073741824.0,
                        snapshot.CommitTotalBytes / 1073741824.0,
                        snapshot.CommitLimitBytes / 1073741824.0,
                        snapshot.CommitHeadroomBytes / 1073741824.0),
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
            if (report == null)
            {
                return;
            }
            if (!IsValidSessionId(report.SessionId))
            {
                throw new ArgumentException(
                    "SessionId must be a GUID in N format.",
                    "report");
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
            lines.Add("MemorySamples=" +
                report.MemorySamples.ToString(CultureInfo.InvariantCulture));
            lines.Add("MemoryReliefAttempts=" +
                report.MemoryReliefAttempts.ToString(CultureInfo.InvariantCulture));
            lines.Add("MemoryReliefSuccesses=" +
                report.MemoryReliefSuccesses.ToString(CultureInfo.InvariantCulture));
            lines.Add("MemoryReliefBytes=" +
                report.MemoryReliefBytes.ToString(CultureInfo.InvariantCulture));
            lines.Add("MinimumAvailableMemoryBytes=" +
                report.MinimumAvailableMemoryBytes.ToString(CultureInfo.InvariantCulture));
            lines.Add("MinimumCommitHeadroomBytes=" +
                report.MinimumCommitHeadroomBytes.ToString(CultureInfo.InvariantCulture));
            lines.Add("PeakGameWorkingSetBytes=" +
                report.PeakGameWorkingSetBytes.ToString(CultureInfo.InvariantCulture));
            lines.Add("PeakGamePrivateBytes=" +
                report.PeakGamePrivateBytes.ToString(CultureInfo.InvariantCulture));
            lines.Add("GameCrashCode=" + Encode(report.GameCrashCode));
            lines.Add("GameCrashModule=" + Encode(report.GameCrashModule));
            lines.Add("GameCrashOffset=" + Encode(report.GameCrashOffset));
            lines.Add("GameCrashUtc=" + (report.GameCrashUtc.HasValue
                ? report.GameCrashUtc.Value.ToString("o", CultureInfo.InvariantCulture)
                : string.Empty));
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
            if (!TryParseInt(values, "Version", out version) ||
                (version != 1 && version != 2))
            {
                return null;
            }

            DateTime startedUtc;
            if (!TryParseDate(values, "StartedUtc", out startedUtc))
            {
                return null;
            }

            string sessionId = Decode(GetValue(values, "SessionId"));
            if (!IsValidSessionId(sessionId))
            {
                return null;
            }

            var report = new BoostSessionReport
            {
                Version = version,
                SessionId = sessionId,
                Trigger = Decode(GetValue(values, "Trigger")),
                Status = Decode(GetValue(values, "Status")),
                StartedUtc = startedUtc,
                GameCrashCode = Decode(GetValue(values, "GameCrashCode")),
                GameCrashModule = Decode(GetValue(values, "GameCrashModule")),
                GameCrashOffset = Decode(GetValue(values, "GameCrashOffset")),
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
            int integerValue;
            if (TryParseInt(values, "MemorySamples", out integerValue))
            {
                report.MemorySamples = Math.Max(0, integerValue);
            }
            if (TryParseInt(values, "MemoryReliefAttempts", out integerValue))
            {
                report.MemoryReliefAttempts = Math.Max(0, integerValue);
            }
            if (TryParseInt(values, "MemoryReliefSuccesses", out integerValue))
            {
                report.MemoryReliefSuccesses = Math.Max(0, integerValue);
            }
            if (TryParseLong(values, "MemoryReliefBytes", out longValue))
            {
                report.MemoryReliefBytes = Math.Max(0, longValue);
            }
            if (TryParseLong(values, "MinimumAvailableMemoryBytes", out longValue))
            {
                report.MinimumAvailableMemoryBytes = Math.Max(0, longValue);
            }
            if (TryParseLong(values, "MinimumCommitHeadroomBytes", out longValue))
            {
                report.MinimumCommitHeadroomBytes = Math.Max(0, longValue);
            }
            if (TryParseLong(values, "PeakGameWorkingSetBytes", out longValue))
            {
                report.PeakGameWorkingSetBytes = Math.Max(0, longValue);
            }
            if (TryParseLong(values, "PeakGamePrivateBytes", out longValue))
            {
                report.PeakGamePrivateBytes = Math.Max(0, longValue);
            }
            DateTime dateValue;
            if (TryParseDate(values, "EndedUtc", out dateValue))
            {
                report.EndedUtc = dateValue;
            }
            if (TryParseDate(values, "GameCrashUtc", out dateValue))
            {
                report.GameCrashUtc = dateValue;
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

        private static bool IsValidSessionId(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || sessionId.Length != 32)
            {
                return false;
            }

            Guid parsed;
            return Guid.TryParseExact(sessionId, "N", out parsed) &&
                string.Equals(
                    parsed.ToString("N"),
                    sessionId,
                    StringComparison.OrdinalIgnoreCase);
        }

        public static void WriteAllTextAtomic(string destination, string content)
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
