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
    public int HandshakeDelayMs { get; init; }
    public int? ArticleBufferSize { get; init; }
    public bool PrewarmConnections { get; init; }
    // Awaits prewarming before the measurement origin instead of racing it at read start.
    public bool WarmStart { get; init; }

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

    public static IReadOnlyList<NntpWholePathScenario> Cold =>
    [
        new("cold-ramp-256mib-w4", NntpWholePathLayer.HttpLike, false, 342, 768 * 1024, 20, 4, 40, 6_000_000, YencCrcValidationMode.Require)
        {
            HandshakeDelayMs = 150,
            ArticleBufferSize = 40,
        },
        new("cold-ramp-256mib-w4-prewarm", NntpWholePathLayer.HttpLike, false, 342, 768 * 1024, 20, 4, 40, 6_000_000, YencCrcValidationMode.Require)
        {
            HandshakeDelayMs = 150,
            ArticleBufferSize = 40,
            PrewarmConnections = true,
        },
    ];

    // Warm, paced connections isolate ordered-delivery stalls from connection-ramp latency.
    public static IReadOnlyList<NntpWholePathScenario> Smoothness =>
    [
        Paced("paced-256mib-w1", batchWidth: 1),
        Paced("paced-256mib-w4", batchWidth: 4),
        Paced("paced-256mib-w8", batchWidth: 8),
        // Fewer connections than the default window can use: scheduling must not
        // depend on spare capacity to stay steady.
        Paced("paced-256mib-w4-4conn", batchWidth: 4, connections: 4),
        // One stripe per buffered article: a full-batch-per-stripe start would fill the whole window.
        Paced("paced-256mib-w4-40conn", batchWidth: 4, connections: 40),
    ];

    private static NntpWholePathScenario Paced(string name, int batchWidth, int connections = 20) =>
        new(name, NntpWholePathLayer.HttpLike, false, 342, 768 * 1024, connections, batchWidth, 40, 6_000_000, YencCrcValidationMode.Require)
        {
            ArticleBufferSize = 40,
            WarmStart = true,
        };

    public static IReadOnlyList<NntpWholePathScenario> ForSet(string set) =>
        set.Equals("quick", StringComparison.OrdinalIgnoreCase)
            ? Quick
            : set.Equals("sustained", StringComparison.OrdinalIgnoreCase)
                ? Sustained
                : set.Equals("profile", StringComparison.OrdinalIgnoreCase)
                    ? Profile
                    : set.Equals("cold", StringComparison.OrdinalIgnoreCase)
                        ? Cold
                        : set.Equals("smoothness", StringComparison.OrdinalIgnoreCase)
                            ? Smoothness
                            : throw new ArgumentException(
                                "--set must be 'quick', 'sustained', 'profile', 'cold', or 'smoothness'.",
                                nameof(set));
}
