using System.Diagnostics;
using System.Globalization;

namespace NzbWebDAV.Utils;

/// <summary>
/// Captures the virtual-address-space constraints that matter when a host uses
/// <c>RLIMIT_AS</c> instead of a cgroup memory limit.
/// </summary>
internal static class AddressSpaceDiagnostics
{
    internal readonly record struct Snapshot(
        long? AddressSpaceLimitBytes,
        long? VirtualMemoryBytes,
        long? GcRegionRangeBytes,
        long? GcRegionSizeBytes,
        long? GcHeapHardLimitBytes,
        long? GcHeapHardLimitPercent,
        long? GcCommittedBytes)
    {
        public long? GcHeapHardLimitLohBytes { get; init; }
        public long? GcHeapHardLimitLohPercent { get; init; }
        public long? WorkingSetBytes { get; init; }
    }

    internal static Snapshot Capture()
    {
        long? virtualMemoryBytes = null;
        long? workingSetBytes = null;
        try
        {
            using var process = Process.GetCurrentProcess();
            virtualMemoryBytes = process.VirtualMemorySize64;
            workingSetBytes = process.WorkingSet64;
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
            // Process diagnostics are optional and must not prevent startup or support-pack collection.
        }

        IReadOnlyDictionary<string, object>? config = null;
        long? committedBytes = null;
        try
        {
            config = GC.GetConfigurationVariables();
            committedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            // GC diagnostics are optional on unsupported runtimes.
        }

        return new Snapshot(
            ReadLinuxAddressSpaceLimit(),
            virtualMemoryBytes,
            GetConfigurationValue(config, "GCRegionRange"),
            GetConfigurationValue(config, "GCRegionSize"),
            GetConfigurationValue(config, "GCHeapHardLimit"),
            GetConfigurationValue(config, "GCHeapHardLimitPercent"),
            committedBytes)
        {
            GcHeapHardLimitLohBytes = GetConfigurationValue(config, "GCHeapHardLimitLOH"),
            GcHeapHardLimitLohPercent = GetConfigurationValue(config, "GCHeapHardLimitLOHPercent"),
            WorkingSetBytes = workingSetBytes,
        };
    }

    internal static long? ParseLinuxAddressSpaceLimit(string limits)
    {
        foreach (var line in limits.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("Max address space", StringComparison.Ordinal)) continue;

            var columns = line["Max address space".Length..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length == 0 || columns[0].Equals("unlimited", StringComparison.OrdinalIgnoreCase))
                return null;

            return long.TryParse(columns[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        return null;
    }

    private static long? ReadLinuxAddressSpaceLimit()
    {
        const string limitsPath = "/proc/self/limits";
        if (!OperatingSystem.IsLinux() || !File.Exists(limitsPath)) return null;

        try
        {
            return ParseLinuxAddressSpaceLimit(File.ReadAllText(limitsPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? GetConfigurationValue(
        IReadOnlyDictionary<string, object>? config,
        string name)
    {
        if (config is null || !config.TryGetValue(name, out var value)) return null;
        var bytes = ToInt64(value);
        return bytes > 0 ? bytes : null;
    }

    private static long ToInt64(object value) => value switch
    {
        long l => l,
        ulong ul => ul > long.MaxValue ? long.MaxValue : (long)ul,
        int i => i,
        uint ui => ui,
        string s when long.TryParse(s, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0,
    };
}
