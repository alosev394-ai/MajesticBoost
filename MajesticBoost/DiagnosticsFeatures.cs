using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MajesticBoost
{
    internal enum DiagnosticPressureLevel
    {
        Unavailable,
        Normal,
        Elevated,
        Critical
    }

    internal sealed class DiagnosticSnapshot
    {
        public DateTime CapturedUtc;

        public bool MemoryAvailable;
        public string MemoryError;
        public long PhysicalTotalBytes;
        public long PhysicalAvailableBytes;
        public long SystemCacheBytes;
        public long CommitUsedBytes;
        public long CommitLimitBytes;
        public long CommitHeadroomBytes;

        public bool PageFileAvailable;
        public string PageFileError;
        public long PageFileAllocatedBytes;
        public long PageFileUsedBytes;
        public long PageFilePeakUsedBytes;

        public bool GpuUsageAvailable;
        public bool GpuBudgetAvailable;
        public bool GpuTotalAvailable;
        public string GpuError;
        public string GpuAdapterNames;
        public string GpuAdapterLuid;
        public long GpuDedicatedUsageBytes;
        public long GpuDedicatedBudgetBytes;
        public long GpuDedicatedTotalBytes;

        public DiagnosticPressureLevel Pressure;
        public string PressureReason;
    }

    internal static class DiagnosticPressureClassifier
    {
        public const long OneGibibyte = 1024L * 1024L * 1024L;
        public const long TwoGibibytes = 2L * 1024L * 1024L * 1024L;

        public static DiagnosticPressureLevel Classify(DiagnosticSnapshot snapshot)
        {
            string ignored;
            return Classify(snapshot, out ignored);
        }

        public static DiagnosticPressureLevel Classify(
            DiagnosticSnapshot snapshot,
            out string reason)
        {
            if (snapshot == null ||
                !snapshot.MemoryAvailable ||
                snapshot.PhysicalTotalBytes <= 0 ||
                snapshot.CommitLimitBytes <= 0)
            {
                reason = "Windows memory metrics are unavailable.";
                return DiagnosticPressureLevel.Unavailable;
            }

            long available = Math.Max(0, snapshot.PhysicalAvailableBytes);
            long commitHeadroom = Math.Max(0, snapshot.CommitHeadroomBytes);
            long physicalCritical = Math.Max(
                OneGibibyte,
                Percentage(snapshot.PhysicalTotalBytes, 8));
            long physicalElevated = Math.Max(
                TwoGibibytes,
                Percentage(snapshot.PhysicalTotalBytes, 15));
            long commitCritical = Math.Max(
                OneGibibyte,
                Percentage(snapshot.CommitLimitBytes, 5));
            long commitElevated = Math.Max(
                TwoGibibytes,
                Percentage(snapshot.CommitLimitBytes, 10));

            if (available <= physicalCritical)
            {
                reason = "Available physical memory is critically low.";
                return DiagnosticPressureLevel.Critical;
            }
            if (commitHeadroom <= commitCritical)
            {
                reason = "Windows commit headroom is critically low.";
                return DiagnosticPressureLevel.Critical;
            }
            if (snapshot.GpuUsageAvailable &&
                snapshot.GpuTotalAvailable &&
                snapshot.GpuDedicatedTotalBytes > 0 &&
                IsAtLeastPercent(
                    snapshot.GpuDedicatedUsageBytes,
                    snapshot.GpuDedicatedTotalBytes,
                    95))
            {
                reason = "Dedicated GPU memory usage is at or above 95% of adapter capacity.";
                return DiagnosticPressureLevel.Critical;
            }

            if (available <= physicalElevated)
            {
                reason = "Available physical memory is below the recommended reserve.";
                return DiagnosticPressureLevel.Elevated;
            }
            if (commitHeadroom <= commitElevated)
            {
                reason = "Windows commit headroom is below the recommended reserve.";
                return DiagnosticPressureLevel.Elevated;
            }
            if (snapshot.GpuUsageAvailable &&
                snapshot.GpuTotalAvailable &&
                snapshot.GpuDedicatedTotalBytes > 0 &&
                IsAtLeastPercent(
                    snapshot.GpuDedicatedUsageBytes,
                    snapshot.GpuDedicatedTotalBytes,
                    90))
            {
                reason = "Dedicated GPU memory usage is above 90% of adapter capacity.";
                return DiagnosticPressureLevel.Elevated;
            }

            reason = "Physical memory and commit reserves are healthy.";
            return DiagnosticPressureLevel.Normal;
        }

        private static long Percentage(long value, int percent)
        {
            if (value <= 0 || percent <= 0)
            {
                return 0;
            }
            return (value / 100L) * percent +
                ((value % 100L) * percent) / 100L;
        }

        private static bool IsAtLeastPercent(long value, long limit, int percent)
        {
            if (value < 0 || limit <= 0 || percent <= 0)
            {
                return false;
            }
            if (limit > long.MaxValue / percent)
            {
                return (double)value / (double)limit >= percent / 100.0;
            }
            return value >= limit * percent / 100L;
        }
    }

    internal static class DiagnosticSnapshotProvider
    {
        private const int MaximumAdapters = 32;
        private const int DxgiAdapterFlagSoftware = 2;
        private const int EnumAdapters1VtableSlot = 12;
        private const int GetDesc1VtableSlot = 10;

        private static readonly Guid Factory1InterfaceId =
            new Guid("770AAE78-F26F-4DBA-A829-253C83D1B387");
        private static readonly Regex GpuAdapterLuidPattern = new Regex(
            @"(?:^|_)luid_0x(?<high>[0-9a-f]{1,8})_0x(?<low>[0-9a-f]{1,8})(?:_|$)",
            RegexOptions.Compiled |
            RegexOptions.CultureInvariant |
            RegexOptions.IgnoreCase);

        private sealed class DxgiAdapterCapacity
        {
            public string LuidKey;
            public string Name;
            public long DedicatedVideoMemoryBytes;
        }

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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DxgiAdapterDescription1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSystemId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public uint AdapterLuidLowPart;
            public int AdapterLuidHighPart;
            public uint Flags;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapters1Delegate(
            IntPtr factory,
            uint adapterIndex,
            out IntPtr adapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetAdapterDescription1Delegate(
            IntPtr adapter,
            out DxgiAdapterDescription1 description);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPerformanceInfo(
            out PerformanceInformationNative information,
            uint size);

        [DllImport("dxgi.dll", PreserveSig = true)]
        private static extern int CreateDXGIFactory1(
            [In] ref Guid interfaceId,
            out IntPtr factory);

        public static DiagnosticSnapshot Capture()
        {
            var snapshot = new DiagnosticSnapshot
            {
                CapturedUtc = DateTime.UtcNow,
                MemoryError = string.Empty,
                PageFileError = string.Empty,
                GpuError = string.Empty,
                GpuAdapterNames = string.Empty,
                GpuAdapterLuid = string.Empty
            };

            CaptureMemory(snapshot);
            CapturePageFile(snapshot);
            CaptureGpu(snapshot);
            snapshot.Pressure = DiagnosticPressureClassifier.Classify(
                snapshot,
                out snapshot.PressureReason);
            return snapshot;
        }

        private static void CaptureMemory(DiagnosticSnapshot snapshot)
        {
            PerformanceInformationNative information;
            uint size = (uint)Marshal.SizeOf(typeof(PerformanceInformationNative));
            try
            {
                if (!GetPerformanceInfo(out information, size))
                {
                    snapshot.MemoryError = "GetPerformanceInfo failed with Win32 error " +
                        Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ".";
                    return;
                }

                snapshot.PhysicalTotalBytes = PagesToBytes(
                    information.PhysicalTotal,
                    information.PageSize);
                snapshot.PhysicalAvailableBytes = PagesToBytes(
                    information.PhysicalAvailable,
                    information.PageSize);
                snapshot.SystemCacheBytes = PagesToBytes(
                    information.SystemCache,
                    information.PageSize);
                snapshot.CommitUsedBytes = PagesToBytes(
                    information.CommitTotal,
                    information.PageSize);
                snapshot.CommitLimitBytes = PagesToBytes(
                    information.CommitLimit,
                    information.PageSize);
                snapshot.CommitHeadroomBytes = Math.Max(
                    0,
                    snapshot.CommitLimitBytes - snapshot.CommitUsedBytes);
                snapshot.MemoryAvailable =
                    snapshot.PhysicalTotalBytes > 0 &&
                    snapshot.CommitLimitBytes > 0;
                if (!snapshot.MemoryAvailable)
                {
                    snapshot.MemoryError = "Windows returned incomplete memory metrics.";
                }
            }
            catch (Exception error)
            {
                snapshot.MemoryError = "Memory metrics are unavailable: " +
                    error.GetType().Name + ".";
            }
        }

        private static void CapturePageFile(DiagnosticSnapshot snapshot)
        {
            try
            {
                ulong allocatedMegabytes = 0;
                ulong usedMegabytes = 0;
                ulong peakMegabytes = 0;
                int count = 0;
                var options = new EnumerationOptions
                {
                    ReturnImmediately = true,
                    Rewindable = false,
                    Timeout = TimeSpan.FromSeconds(2)
                };
                using (var searcher = new ManagementObjectSearcher(
                    new ManagementScope(@"\\.\root\cimv2"),
                    new ObjectQuery(
                        "SELECT AllocatedBaseSize, CurrentUsage, PeakUsage " +
                        "FROM Win32_PageFileUsage"),
                    options))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject item in results)
                    {
                        allocatedMegabytes = AddSaturated(
                            allocatedMegabytes,
                            ReadUInt64(item, "AllocatedBaseSize"));
                        usedMegabytes = AddSaturated(
                            usedMegabytes,
                            ReadUInt64(item, "CurrentUsage"));
                        peakMegabytes = AddSaturated(
                            peakMegabytes,
                            ReadUInt64(item, "PeakUsage"));
                        count++;
                    }
                }

                snapshot.PageFileAllocatedBytes = MegabytesToBytes(allocatedMegabytes);
                snapshot.PageFileUsedBytes = MegabytesToBytes(usedMegabytes);
                snapshot.PageFilePeakUsedBytes = MegabytesToBytes(peakMegabytes);
                snapshot.PageFileAvailable = count > 0;
                if (!snapshot.PageFileAvailable)
                {
                    snapshot.PageFileError = "No active Windows page file was reported.";
                }
            }
            catch (Exception error)
            {
                snapshot.PageFileError = "Page-file metrics are unavailable: " +
                    error.GetType().Name + ".";
            }
        }

        private static void CaptureGpu(DiagnosticSnapshot snapshot)
        {
            snapshot.GpuBudgetAvailable = false;
            snapshot.GpuDedicatedBudgetBytes = 0;

            string[] usageInstanceNames;
            long[] usageValues;
            string counterError;
            bool usageSamplesAvailable = TryGetDedicatedGpuUsageSamples(
                out usageInstanceNames,
                out usageValues,
                out counterError);

            List<DxgiAdapterCapacity> adapters;
            string dxgiError;
            bool capacityAvailable = TryGetDxgiAdapterCapacities(
                out adapters,
                out dxgiError);

            int selectedAdapterIndex = -1;
            long selectedUsageBytes = 0;
            bool selected = usageSamplesAvailable &&
                capacityAvailable &&
                TrySelectMostPressuredAdapter(
                    usageInstanceNames,
                    usageValues,
                    adapters.Select(item => item.LuidKey).ToArray(),
                    adapters.Select(item => item.DedicatedVideoMemoryBytes).ToArray(),
                    out selectedAdapterIndex,
                    out selectedUsageBytes);

            if (selected)
            {
                DxgiAdapterCapacity adapter = adapters[selectedAdapterIndex];
                snapshot.GpuUsageAvailable = true;
                snapshot.GpuTotalAvailable = true;
                snapshot.GpuDedicatedUsageBytes = selectedUsageBytes;
                snapshot.GpuDedicatedTotalBytes =
                    adapter.DedicatedVideoMemoryBytes;
                snapshot.GpuAdapterNames = adapter.Name;
                snapshot.GpuAdapterLuid = adapter.LuidKey;
            }

            var errors = new List<string>();
            if (!usageSamplesAvailable && !string.IsNullOrEmpty(counterError))
            {
                errors.Add(counterError);
            }
            if (!capacityAvailable && !string.IsNullOrEmpty(dxgiError))
            {
                errors.Add(dxgiError);
            }
            if (usageSamplesAvailable && capacityAvailable && !selected)
            {
                errors.Add(
                    "GPU usage samples could not be matched to a hardware " +
                    "adapter with dedicated capacity by LUID.");
            }
            snapshot.GpuError = string.Join(" ", errors.Distinct().ToArray());
        }

        private static bool TryGetDedicatedGpuUsageSamples(
            out string[] instanceNames,
            out long[] usageValues,
            out string error)
        {
            var sampledInstances = new List<string>();
            var sampledValues = new List<long>();
            instanceNames = new string[0];
            usageValues = new long[0];
            error = string.Empty;
            try
            {
                var category = new PerformanceCounterCategory("GPU Adapter Memory");
                foreach (string instance in category.GetInstanceNames())
                {
                    try
                    {
                        using (var counter = new PerformanceCounter(
                            "GPU Adapter Memory",
                            "Dedicated Usage",
                            instance,
                            true))
                        {
                            long rawValue = counter.NextSample().RawValue;
                            if (rawValue >= 0)
                            {
                                sampledInstances.Add(instance ?? string.Empty);
                                sampledValues.Add(rawValue);
                            }
                        }
                    }
                    catch
                    {
                        // A GPU instance can disappear between enumeration and sampling.
                    }
                }

                instanceNames = sampledInstances.ToArray();
                usageValues = sampledValues.ToArray();
                if (instanceNames.Length == 0)
                {
                    error = "The GPU Adapter Memory performance counter returned no samples.";
                }
                return instanceNames.Length > 0;
            }
            catch (Exception captureError)
            {
                error = "GPU usage metrics are unavailable: " +
                    captureError.GetType().Name + ".";
                return false;
            }
        }

        private static bool TryGetDxgiAdapterCapacities(
            out List<DxgiAdapterCapacity> adapters,
            out string error)
        {
            adapters = new List<DxgiAdapterCapacity>();
            error = string.Empty;
            IntPtr factory = IntPtr.Zero;
            try
            {
                Guid factoryId = Factory1InterfaceId;
                int createResult = CreateDXGIFactory1(ref factoryId, out factory);
                if (createResult < 0 || factory == IntPtr.Zero)
                {
                    error = "DXGI factory creation failed (HRESULT 0x" +
                        createResult.ToString("X8", CultureInfo.InvariantCulture) + ").";
                    return false;
                }

                var enumerate = (EnumAdapters1Delegate)Marshal.GetDelegateForFunctionPointer(
                    GetVtableMethod(factory, EnumAdapters1VtableSlot),
                    typeof(EnumAdapters1Delegate));
                for (uint index = 0; index < MaximumAdapters; index++)
                {
                    IntPtr adapter = IntPtr.Zero;
                    int enumerateResult = enumerate(factory, index, out adapter);
                    if (enumerateResult < 0 || adapter == IntPtr.Zero)
                    {
                        break;
                    }

                    try
                    {
                        DxgiAdapterDescription1 description;
                        var getDescription =
                            (GetAdapterDescription1Delegate)Marshal.GetDelegateForFunctionPointer(
                                GetVtableMethod(adapter, GetDesc1VtableSlot),
                                typeof(GetAdapterDescription1Delegate));
                        int descriptionResult = getDescription(adapter, out description);
                        if (descriptionResult < 0 ||
                            (description.Flags & DxgiAdapterFlagSoftware) != 0)
                        {
                            continue;
                        }

                        long adapterTotal = UIntPtrToInt64(
                            description.DedicatedVideoMemory);
                        if (adapterTotal <= 0)
                        {
                            continue;
                        }

                        string name = NormalizeAdapterName(description.Description);
                        adapters.Add(new DxgiAdapterCapacity
                        {
                            LuidKey = FormatLuidKey(
                                description.AdapterLuidHighPart,
                                description.AdapterLuidLowPart),
                            Name = string.IsNullOrWhiteSpace(name)
                                ? "Hardware GPU"
                                : name,
                            DedicatedVideoMemoryBytes = adapterTotal
                        });
                    }
                    finally
                    {
                        Marshal.Release(adapter);
                    }
                }

                if (adapters.Count == 0)
                {
                    error = "DXGI did not expose dedicated capacity for a hardware adapter.";
                }
                return adapters.Count > 0;
            }
            catch (Exception captureError)
            {
                error = "GPU capacity metrics are unavailable: " +
                    captureError.GetType().Name + ".";
                adapters.Clear();
                return false;
            }
            finally
            {
                if (factory != IntPtr.Zero)
                {
                    Marshal.Release(factory);
                }
            }
        }

        internal static bool TrySelectMostPressuredAdapter(
            string[] usageInstanceNames,
            long[] usageValues,
            string[] adapterLuidKeys,
            long[] adapterTotals,
            out int selectedAdapterIndex,
            out long selectedUsageBytes)
        {
            selectedAdapterIndex = -1;
            selectedUsageBytes = 0;
            if (usageInstanceNames == null ||
                usageValues == null ||
                adapterLuidKeys == null ||
                adapterTotals == null ||
                usageInstanceNames.Length != usageValues.Length ||
                adapterLuidKeys.Length != adapterTotals.Length)
            {
                return false;
            }

            var usageByLuid = new Dictionary<string, long>(
                StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < usageInstanceNames.Length; index++)
            {
                string luidKey;
                if (usageValues[index] < 0 ||
                    !TryParseGpuAdapterLuid(
                        usageInstanceNames[index],
                        out luidKey))
                {
                    continue;
                }

                long existing;
                usageByLuid.TryGetValue(luidKey, out existing);
                usageByLuid[luidKey] = AddSaturated(
                    existing,
                    usageValues[index]);
            }

            double highestPressure = double.NegativeInfinity;
            for (int index = 0; index < adapterLuidKeys.Length; index++)
            {
                long total = adapterTotals[index];
                long usage;
                if (total <= 0 ||
                    string.IsNullOrWhiteSpace(adapterLuidKeys[index]) ||
                    !usageByLuid.TryGetValue(adapterLuidKeys[index], out usage))
                {
                    continue;
                }

                double pressure = (double)usage / (double)total;
                if (selectedAdapterIndex < 0 || pressure > highestPressure)
                {
                    highestPressure = pressure;
                    selectedAdapterIndex = index;
                    selectedUsageBytes = usage;
                }
            }

            return selectedAdapterIndex >= 0;
        }

        private static bool TryParseGpuAdapterLuid(
            string instanceName,
            out string luidKey)
        {
            luidKey = string.Empty;
            if (string.IsNullOrWhiteSpace(instanceName))
            {
                return false;
            }

            Match match = GpuAdapterLuidPattern.Match(instanceName);
            uint highPart;
            uint lowPart;
            if (!match.Success ||
                !uint.TryParse(
                    match.Groups["high"].Value,
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out highPart) ||
                !uint.TryParse(
                    match.Groups["low"].Value,
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out lowPart))
            {
                return false;
            }

            luidKey = FormatLuidKey(unchecked((int)highPart), lowPart);
            return true;
        }

        private static string FormatLuidKey(int highPart, uint lowPart)
        {
            return unchecked((uint)highPart).ToString(
                    "X8",
                    CultureInfo.InvariantCulture) +
                lowPart.ToString("X8", CultureInfo.InvariantCulture);
        }

        private static IntPtr GetVtableMethod(IntPtr instance, int slot)
        {
            if (instance == IntPtr.Zero || slot < 0)
            {
                throw new ArgumentException("Invalid COM instance or vtable slot.");
            }
            IntPtr vtable = Marshal.ReadIntPtr(instance);
            if (vtable == IntPtr.Zero)
            {
                throw new InvalidOperationException("The COM vtable is unavailable.");
            }
            IntPtr method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            if (method == IntPtr.Zero)
            {
                throw new MissingMethodException("The requested COM method is unavailable.");
            }
            return method;
        }

        private static string NormalizeAdapterName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            string normalized = Regex.Replace(value.Trim(), @"\s+", " ");
            return normalized.Length <= 128
                ? normalized
                : normalized.Substring(0, 128);
        }

        private static ulong ReadUInt64(ManagementBaseObject item, string property)
        {
            if (item == null)
            {
                return 0;
            }
            object value = item[property];
            if (value == null)
            {
                return 0;
            }
            try
            {
                return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static long PagesToBytes(UIntPtr pages, UIntPtr pageSize)
        {
            ulong count = pages.ToUInt64();
            ulong size = pageSize.ToUInt64();
            if (count == 0 || size == 0)
            {
                return 0;
            }
            return count > (ulong)long.MaxValue / size
                ? long.MaxValue
                : (long)(count * size);
        }

        private static long MegabytesToBytes(ulong megabytes)
        {
            const ulong bytesPerMegabyte = 1024UL * 1024UL;
            return megabytes > (ulong)long.MaxValue / bytesPerMegabyte
                ? long.MaxValue
                : (long)(megabytes * bytesPerMegabyte);
        }

        private static long UIntPtrToInt64(UIntPtr value)
        {
            return UInt64ToInt64(value.ToUInt64());
        }

        private static long UInt64ToInt64(ulong value)
        {
            return value > (ulong)long.MaxValue ? long.MaxValue : (long)value;
        }

        private static ulong AddSaturated(ulong left, ulong right)
        {
            return ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
        }

        private static long AddSaturated(long left, long right)
        {
            if (right <= 0)
            {
                return left;
            }
            return left > long.MaxValue - right ? long.MaxValue : left + right;
        }
    }

    internal static class DiagnosticSessionHistory
    {
        public const int MaximumSessionCount = 10;
        private const int MaximumCandidateFiles = 256;
        private const long MaximumReportBytes = 1024L * 1024L;

        public static List<BoostSessionReport> LoadRecent(int maximumCount)
        {
            return LoadRecentFromDirectory(
                BoostSessionReportStore.SessionsDirectory,
                maximumCount);
        }

        internal static List<BoostSessionReport> LoadRecentFromDirectory(
            string sessionsDirectory,
            int maximumCount)
        {
            var reports = new List<BoostSessionReport>();
            int count = Math.Max(0, Math.Min(MaximumSessionCount, maximumCount));
            if (count == 0 || string.IsNullOrWhiteSpace(sessionsDirectory))
            {
                return reports;
            }

            try
            {
                var directory = new DirectoryInfo(sessionsDirectory);
                if (!directory.Exists ||
                    (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return reports;
                }

                string root = Path.GetFullPath(directory.FullName)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                var candidates = new List<BoostSessionReport>();
                int inspected = 0;
                foreach (FileInfo file in directory.EnumerateFiles("session-*.report"))
                {
                    if (inspected >= MaximumCandidateFiles)
                    {
                        break;
                    }
                    inspected++;

                    string sessionId;
                    if (!TryGetSessionId(file.Name, out sessionId) ||
                        file.Length <= 0 ||
                        file.Length > MaximumReportBytes ||
                        (file.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    string fullPath = Path.GetFullPath(file.FullName);
                    if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    BoostSessionReport report = BoostSessionReportStore.Load(fullPath);
                    if (report != null &&
                        string.Equals(
                            report.SessionId,
                            sessionId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(report);
                        if (candidates.Count > count)
                        {
                            BoostSessionReport oldest = candidates
                                .OrderBy(item => item.StartedUtc)
                                .ThenBy(item => item.EndedUtc ?? DateTime.MinValue)
                                .First();
                            candidates.Remove(oldest);
                        }
                    }
                }

                reports.AddRange(
                    candidates
                        .OrderByDescending(item => item.StartedUtc)
                        .ThenByDescending(item => item.EndedUtc ?? DateTime.MinValue)
                        .Take(count));
            }
            catch
            {
                // History is optional and must never block the main Boost UI.
            }
            return reports;
        }

        private static bool TryGetSessionId(string fileName, out string sessionId)
        {
            sessionId = string.Empty;
            const string prefix = "session-";
            const string suffix = ".report";
            if (string.IsNullOrEmpty(fileName) ||
                !fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string candidate = fileName.Substring(
                prefix.Length,
                fileName.Length - prefix.Length - suffix.Length);
            if (candidate.Length != 32)
            {
                return false;
            }

            Guid parsed;
            if (!Guid.TryParseExact(candidate, "N", out parsed) ||
                !string.Equals(
                    parsed.ToString("N"),
                    candidate,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            sessionId = candidate;
            return true;
        }
    }

    internal static class DiagnosticExportBuilder
    {
        public const int MaximumExportCharacters = 64 * 1024;
        private const int MaximumActionsPerSession = 12;
        private static readonly Regex EmailPattern = new Regex(
            @"(?i)(?<![\w.+-])[\w.+-]+@[\w.-]+(?![\w.-])",
            RegexOptions.CultureInvariant);
        private static readonly Regex WindowsPathPattern = new Regex(
            @"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n\t<>""|?*]+",
            RegexOptions.CultureInvariant);
        private static readonly Regex SecretPattern = new Regex(
            @"(?i)\b(token|secret|password|passwd|authorization|cookie|api[_-]?key)" +
            @"\b\s*[:=]\s*[^\r\n]+",
            RegexOptions.CultureInvariant);

        public static string BuildSafeReport(
            DiagnosticSnapshot snapshot,
            IEnumerable<BoostSessionReport> sessions,
            string notes)
        {
            var builder = new StringBuilder(8192);
            builder.AppendLine("MAJESTIC BOOST SAFE DIAGNOSTIC REPORT");
            builder.AppendLine("GeneratedUtc=" + DateTime.UtcNow.ToString(
                "o",
                CultureInfo.InvariantCulture));

            AppendSnapshot(builder, snapshot);
            if (!string.IsNullOrWhiteSpace(notes))
            {
                builder.AppendLine();
                builder.AppendLine("[NOTES]");
                builder.AppendLine(SanitizeText(notes));
            }

            builder.AppendLine();
            builder.AppendLine("[RECENT SESSIONS]");
            int sessionCount = 0;
            foreach (BoostSessionReport report in
                (sessions ?? Enumerable.Empty<BoostSessionReport>())
                    .Where(item => item != null)
                    .Take(DiagnosticSessionHistory.MaximumSessionCount))
            {
                AppendSession(builder, report, sessionCount + 1);
                sessionCount++;
                if (builder.Length >= MaximumExportCharacters)
                {
                    break;
                }
            }
            if (sessionCount == 0)
            {
                builder.AppendLine("None");
            }

            string safe = SanitizeText(builder.ToString());
            if (safe.Length <= MaximumExportCharacters)
            {
                return safe;
            }

            const string suffix = "\r\n[REPORT TRUNCATED]\r\n";
            return safe.Substring(
                0,
                MaximumExportCharacters - suffix.Length) + suffix;
        }

        public static void WriteSafeReport(
            string destinationPath,
            DiagnosticSnapshot snapshot,
            IEnumerable<BoostSessionReport> sessions,
            string notes)
        {
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                throw new ArgumentException(
                    "A diagnostic report destination is required.",
                    "destinationPath");
            }

            string fullPath = Path.GetFullPath(destinationPath);
            if (string.IsNullOrEmpty(Path.GetFileName(fullPath)))
            {
                throw new ArgumentException(
                    "The diagnostic report destination must be a file.",
                    "destinationPath");
            }
            BoostSessionReportStore.WriteAllTextAtomic(
                fullPath,
                BuildSafeReport(snapshot, sessions, notes));
        }

        internal static string SanitizeText(string value)
        {
            string safe = value ?? string.Empty;
            int maximumInputCharacters = MaximumExportCharacters * 4;
            if (safe.Length > maximumInputCharacters)
            {
                safe = safe.Substring(0, maximumInputCharacters);
            }
            safe = WindowsPathPattern.Replace(safe, "<path>");
            safe = ReplaceKnownPath(
                safe,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "<user-profile>");
            safe = ReplaceKnownPath(
                safe,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "<local-app-data>");
            safe = ReplaceKnownPath(
                safe,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "<app-data>");
            safe = ReplaceKnownPath(safe, Path.GetTempPath(), "<temp>");

            string userName = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(userName))
            {
                safe = Regex.Replace(
                    safe,
                    @"(?<![\p{L}\p{Nd}_.-])" + Regex.Escape(userName) +
                    @"(?![\p{L}\p{Nd}_.-])",
                    "<user>",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            safe = EmailPattern.Replace(safe, "<email>");
            safe = SecretPattern.Replace(safe, "$1=<redacted>");

            var sanitized = new StringBuilder(Math.Min(safe.Length, MaximumExportCharacters));
            foreach (char character in safe)
            {
                if (character == '\r' ||
                    character == '\n' ||
                    character == '\t' ||
                    !char.IsControl(character))
                {
                    sanitized.Append(character);
                }
                else
                {
                    sanitized.Append('?');
                }
                if (sanitized.Length >= MaximumExportCharacters)
                {
                    break;
                }
            }
            return sanitized.ToString();
        }

        private static void AppendSnapshot(
            StringBuilder builder,
            DiagnosticSnapshot snapshot)
        {
            builder.AppendLine();
            builder.AppendLine("[SYSTEM SNAPSHOT]");
            if (snapshot == null)
            {
                builder.AppendLine("Available=False");
                return;
            }

            builder.AppendLine("CapturedUtc=" + snapshot.CapturedUtc.ToString(
                "o",
                CultureInfo.InvariantCulture));
            builder.AppendLine("Pressure=" + snapshot.Pressure);
            builder.AppendLine("PressureReason=" + SanitizeText(snapshot.PressureReason));
            builder.AppendLine("MemoryAvailable=" + snapshot.MemoryAvailable);
            builder.AppendLine("PhysicalTotalBytes=" + Format(snapshot.PhysicalTotalBytes));
            builder.AppendLine("PhysicalAvailableBytes=" +
                Format(snapshot.PhysicalAvailableBytes));
            builder.AppendLine("SystemCacheBytes=" + Format(snapshot.SystemCacheBytes));
            builder.AppendLine("CommitUsedBytes=" + Format(snapshot.CommitUsedBytes));
            builder.AppendLine("CommitLimitBytes=" + Format(snapshot.CommitLimitBytes));
            builder.AppendLine("CommitHeadroomBytes=" +
                Format(snapshot.CommitHeadroomBytes));
            builder.AppendLine("MemoryError=" + SanitizeText(snapshot.MemoryError));

            builder.AppendLine("PageFileAvailable=" + snapshot.PageFileAvailable);
            builder.AppendLine("PageFileAllocatedBytes=" +
                Format(snapshot.PageFileAllocatedBytes));
            builder.AppendLine("PageFileUsedBytes=" + Format(snapshot.PageFileUsedBytes));
            builder.AppendLine("PageFilePeakUsedBytes=" +
                Format(snapshot.PageFilePeakUsedBytes));
            builder.AppendLine("PageFileError=" + SanitizeText(snapshot.PageFileError));

            builder.AppendLine("GpuUsageAvailable=" + snapshot.GpuUsageAvailable);
            builder.AppendLine("GpuBudgetAvailable=" + snapshot.GpuBudgetAvailable);
            builder.AppendLine("GpuTotalAvailable=" + snapshot.GpuTotalAvailable);
            builder.AppendLine(
                "GpuCapacityBasis=Matching DXGI DedicatedVideoMemory for selected LUID");
            builder.AppendLine(
                "GpuBudgetNote=Not collected; DXGI process budget is not a system-wide capacity");
            builder.AppendLine("GpuSelectedAdapter=" +
                SanitizeText(snapshot.GpuAdapterNames));
            builder.AppendLine("GpuSelectedAdapterLuid=" +
                SanitizeText(snapshot.GpuAdapterLuid));
            builder.AppendLine("GpuSystemDedicatedUsageBytes=" +
                Format(snapshot.GpuDedicatedUsageBytes));
            builder.AppendLine("GpuMatchingDedicatedCapacityBytes=" +
                Format(snapshot.GpuDedicatedTotalBytes));
            builder.AppendLine("GpuError=" + SanitizeText(snapshot.GpuError));
        }

        private static void AppendSession(
            StringBuilder builder,
            BoostSessionReport report,
            int index)
        {
            builder.AppendLine();
            builder.AppendLine("Session" + index.ToString(
                CultureInfo.InvariantCulture) + ":");
            builder.AppendLine("  Id=" + SanitizeText(report.SessionId));
            builder.AppendLine("  StartedUtc=" + report.StartedUtc.ToString(
                "o",
                CultureInfo.InvariantCulture));
            builder.AppendLine("  EndedUtc=" + (report.EndedUtc.HasValue
                ? report.EndedUtc.Value.ToString("o", CultureInfo.InvariantCulture)
                : string.Empty));
            builder.AppendLine("  Trigger=" + SanitizeText(report.Trigger));
            builder.AppendLine("  Status=" + SanitizeText(report.Status));
            builder.AppendLine("  StopReason=" + SanitizeText(report.StopReason));
            builder.AppendLine("  MinimumAvailableMemoryBytes=" +
                Format(report.MinimumAvailableMemoryBytes));
            builder.AppendLine("  MinimumCommitHeadroomBytes=" +
                Format(report.MinimumCommitHeadroomBytes));
            builder.AppendLine("  PeakGameWorkingSetBytes=" +
                Format(report.PeakGameWorkingSetBytes));
            builder.AppendLine("  PeakGamePrivateBytes=" +
                Format(report.PeakGamePrivateBytes));
            builder.AppendLine("  DiagnosticSamples=" +
                report.DiagnosticSamples.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("  WorstResourcePressure=" +
                SanitizeText(report.WorstResourcePressure));
            builder.AppendLine("  PhysicalMemoryTotalBytes=" +
                Format(report.PhysicalMemoryTotalBytes));
            builder.AppendLine("  CommitLimitBytes=" +
                Format(report.CommitLimitBytes));
            builder.AppendLine("  PageFileAllocatedBytes=" +
                Format(report.PageFileAllocatedBytes));
            builder.AppendLine("  PeakPageFileUsedBytes=" +
                Format(report.PeakPageFileUsedBytes));
            builder.AppendLine("  GpuSelectedAdapter=" +
                SanitizeText(report.GpuAdapterNames));
            builder.AppendLine("  GpuSelectedAdapterLuid=" +
                SanitizeText(report.GpuAdapterLuid));
            builder.AppendLine("  GpuMatchingDedicatedCapacityBytes=" +
                Format(report.GpuDedicatedTotalBytes));
            builder.AppendLine("  LegacyGpuBudgetBytes=" +
                Format(report.GpuDedicatedBudgetBytes));
            builder.AppendLine("  PeakGpuSystemDedicatedUsageBytes=" +
                Format(report.PeakGpuDedicatedUsageBytes));
            builder.AppendLine("  MinimumGpuDedicatedHeadroomBytes=" +
                Format(report.MinimumGpuDedicatedHeadroomBytes));
            builder.AppendLine("  GameCrashCode=" + SanitizeText(report.GameCrashCode));
            builder.AppendLine("  GameCrashModule=" +
                SanitizeText(report.GameCrashModule));

            if (report.Performance != null)
            {
                builder.AppendLine("  AverageFps=" +
                    report.Performance.AverageFps.ToString(
                        "R",
                        CultureInfo.InvariantCulture));
                builder.AppendLine("  OnePercentLowFps=" +
                    report.Performance.OnePercentLowFps.ToString(
                        "R",
                        CultureInfo.InvariantCulture));
                builder.AppendLine("  P99FrameTimeMs=" +
                    report.Performance.P99FrameTimeMs.ToString(
                        "R",
                        CultureInfo.InvariantCulture));
            }

            int actionIndex = 0;
            foreach (BoostActionRecord action in
                (report.Actions ?? new List<BoostActionRecord>())
                    .Where(item => item != null)
                    .Take(MaximumActionsPerSession))
            {
                actionIndex++;
                builder.AppendLine(
                    "  Action" + actionIndex.ToString(CultureInfo.InvariantCulture) +
                    "=" + action.Outcome + " | " + SanitizeText(action.Title));
            }
        }

        private static string ReplaceKnownPath(
            string input,
            string path,
            string replacement)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return input;
            }

            string result = Regex.Replace(
                input,
                Regex.Escape(path.TrimEnd('\\', '/')),
                replacement,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            string slashVariant = path
                .TrimEnd('\\', '/')
                .Replace('\\', '/');
            return Regex.Replace(
                result,
                Regex.Escape(slashVariant),
                replacement,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string Format(long value)
        {
            return Math.Max(0, value).ToString(CultureInfo.InvariantCulture);
        }
    }
}
