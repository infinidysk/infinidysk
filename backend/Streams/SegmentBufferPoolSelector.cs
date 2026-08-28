namespace NzbWebDAV.Streams;

internal static class SegmentBufferPoolSelector
{
    internal const string EnvironmentVariableName = "NZBDAV_SEGMENT_BUFFER_POOL";
    internal const string BoundedLegacyValue = "bounded-legacy";
    internal const string BoundedCapacityValue = "bounded-capacity";
    internal const string SharedValue = "shared";

    internal enum Mode
    {
        BoundedLegacy,
        BoundedCapacity,
        Shared,
    }

    internal static Mode Resolve(string? value, out bool unknownValue)
    {
        unknownValue = false;
        if (string.IsNullOrWhiteSpace(value))
            return Mode.BoundedLegacy;

        value = value.Trim();
        if (value.Equals(SharedValue, StringComparison.OrdinalIgnoreCase))
            return Mode.Shared;
        if (value.Equals(BoundedLegacyValue, StringComparison.OrdinalIgnoreCase))
            return Mode.BoundedLegacy;
        if (value.Equals(BoundedCapacityValue, StringComparison.OrdinalIgnoreCase))
            return Mode.BoundedCapacity;

        unknownValue = true;
        return Mode.BoundedLegacy;
    }

    internal static string ToLogValue(Mode mode) => mode switch
    {
        Mode.BoundedLegacy => BoundedLegacyValue,
        Mode.BoundedCapacity => BoundedCapacityValue,
        Mode.Shared => SharedValue,
        _ => BoundedLegacyValue,
    };
}
