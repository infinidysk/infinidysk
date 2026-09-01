using UsenetSharp.Clients;

namespace NzbWebDAV.Benchmarks;

internal enum NntpWholePathLayer
{
    Transport,
    Provider,
    BufferedStream,
    HttpLike,
}

internal sealed record NntpWholePathScenario(
    string Name,
    NntpWholePathLayer Layer,
    bool UseTls,
    int ArticleCount,
    int DecodedArticleBytes,
    int ConnectionCount,
    int BatchWidth,
    int RoundTripDelayMs,
    long? BandwidthBytesPerSecond,
    YencCrcValidationMode CrcValidation)
{
    public static IReadOnlyList<NntpWholePathScenario> Quick =>
    [
        new("plain-transport-w4", NntpWholePathLayer.Transport, false, 8, 256 * 1024, 1, 4, 0, null, YencCrcValidationMode.Require),
        new("plain-provider-w4", NntpWholePathLayer.Provider, false, 8, 256 * 1024, 4, 4, 0, null, YencCrcValidationMode.Require),
        new("plain-buffered-w1", NntpWholePathLayer.BufferedStream, false, 8, 256 * 1024, 4, 1, 0, null, YencCrcValidationMode.Require),
        new("plain-buffered-w4", NntpWholePathLayer.BufferedStream, false, 8, 256 * 1024, 4, 4, 0, null, YencCrcValidationMode.Require),
        new("plain-http-like-w4", NntpWholePathLayer.HttpLike, false, 8, 256 * 1024, 4, 4, 0, null, YencCrcValidationMode.Require),
    ];

    public static IReadOnlyList<NntpWholePathScenario> Sustained =>
    [
        new("plain-buffered-w1", NntpWholePathLayer.BufferedStream, false, 256, 4 * 1024 * 1024, 20, 1, 0, null, YencCrcValidationMode.Require),
        new("plain-buffered-w2", NntpWholePathLayer.BufferedStream, false, 256, 4 * 1024 * 1024, 20, 2, 0, null, YencCrcValidationMode.Require),
        new("plain-buffered-w4", NntpWholePathLayer.BufferedStream, false, 256, 4 * 1024 * 1024, 20, 4, 0, null, YencCrcValidationMode.Require),
        new("plain-buffered-w8", NntpWholePathLayer.BufferedStream, false, 256, 4 * 1024 * 1024, 20, 8, 0, null, YencCrcValidationMode.Require),
    ];

    public static IReadOnlyList<NntpWholePathScenario> Profile =>
    [
        new("plain-http-like-w4", NntpWholePathLayer.HttpLike, false, 64, 4 * 1024 * 1024, 20, 4, 0, null, YencCrcValidationMode.Require),
    ];

    public static IReadOnlyList<NntpWholePathScenario> ForSet(string set) =>
        set.Equals("quick", StringComparison.OrdinalIgnoreCase)
            ? Quick
            : set.Equals("sustained", StringComparison.OrdinalIgnoreCase)
                ? Sustained
                : set.Equals("profile", StringComparison.OrdinalIgnoreCase)
                    ? Profile
                    : throw new ArgumentException(
                        "--set must be 'quick', 'sustained', or 'profile'.",
                        nameof(set));
}
