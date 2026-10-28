namespace NzbWebDAV.Services.Catalog;

/// <summary>
/// Tunables for the HLS compatibility path. Registered as a singleton so production keeps
/// the 2 GiB cache bound while tests construct small limits with an explicit clock.
/// </summary>
public sealed class CatalogTranscodeOptions
{
    /// <summary>Hard ceiling for completed segment files on disk (2 GiB).</summary>
    public const long DefaultMaxCacheBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Completed segments idle for this long are evicted by the cleanup sweep.</summary>
    public static readonly TimeSpan SegmentIdleExpiry = TimeSpan.FromHours(24);

    /// <summary>Temporary files a killed or failed encode left behind are removed after this.</summary>
    public static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromHours(1);

    public long MaxCacheBytes { get; init; } = DefaultMaxCacheBytes;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
