namespace NzbWebDAV.Benchmarks;

internal static class PerformanceReportCli
{
    public static async Task<bool> TryHandleAsync(string[] args)
    {
        if (args.Length == 0)
            return false;

        string? report = null;
        string? jsonPath = null;
        string scenarioSet = "quick";
        string? scenarioName = null;
        LoopbackServerArguments? serverArguments = null;
        for (var i = 0; i < args.Length;)
        {
            var arg = args[i];
            if (arg is "--streaming-report" or "--sab-api-report" or "--nntp-whole-path-report")
            {
                if (report is not null)
                    throw new ArgumentException("Multiple performance reports in one invocation.");
                report = arg switch
                {
                    "--streaming-report" => "streaming",
                    "--sab-api-report" => "sab-api",
                    _ => "nntp-whole-path",
                };
                i++;
                continue;
            }

            if (arg == "--nntp-loopback-server")
            {
                if (report is not null || serverArguments is not null)
                    throw new ArgumentException("Multiple performance reports in one invocation.");
                serverArguments = ParseLoopbackServerArguments(args[(i + 1)..]);
                break;
            }

            if (arg == "--json")
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException("--json requires a file path.");
                jsonPath = args[i + 1];
                i += 2;
                continue;
            }

            if (arg == "--set")
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException("--set requires 'quick', 'sustained', or 'profile'.");
                scenarioSet = args[i + 1];
                i += 2;
                continue;
            }

            if (arg == "--scenario")
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException("--scenario requires a scenario name.");
                scenarioName = args[i + 1];
                i += 2;
                continue;
            }

            if (report is not null || jsonPath is not null || scenarioName is not null || scenarioSet != "quick")
                throw new ArgumentException($"Unexpected argument '{arg}'.");
            return false;
        }

        if (serverArguments is not null)
        {
            await NntpWholePathReport.RunChildServerAsync(serverArguments).ConfigureAwait(false);
            return true;
        }

        if (report is null)
        {
            if (jsonPath is not null)
                throw new ArgumentException(
                    "--json requires --streaming-report, --sab-api-report, or --nntp-whole-path-report.");
            if (scenarioName is not null || scenarioSet != "quick")
                throw new ArgumentException("--set and --scenario require --nntp-whole-path-report.");
            return false;
        }

        if (report == "streaming")
        {
            await RepeatableStreamingReport.RunAsync(jsonPath).ConfigureAwait(false);
            return true;
        }

        if (report == "nntp-whole-path")
        {
            await NntpWholePathReport.RunAsync(jsonPath, scenarioSet, scenarioName).ConfigureAwait(false);
            return true;
        }

        if (scenarioName is not null || scenarioSet != "quick")
            throw new ArgumentException("--set and --scenario require --nntp-whole-path-report.");
        if (jsonPath is null)
            throw new ArgumentException("--sab-api-report requires --json <path>.");
        await SabApiReport.RunAsync(jsonPath).ConfigureAwait(false);
        return true;
    }

    private static LoopbackServerArguments ParseLoopbackServerArguments(string[] args)
    {
        var articleCount = 0;
        var articleBytes = 0;
        var seed = NntpWholePathReport.CorpusSeed;
        var roundTripDelayMs = 0;
        long? bandwidthBytesPerSecond = null;
        string? countersPath = null;
        var missingIds = new List<string>();

        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException($"{args[i]} requires a value.");
            var value = args[i + 1];
            switch (args[i])
            {
                case "--articles":
                    articleCount = ParsePositiveInt(args[i], value);
                    break;
                case "--article-bytes":
                    articleBytes = ParsePositiveInt(args[i], value);
                    break;
                case "--seed":
                    seed = ParseInt(args[i], value);
                    break;
                case "--rtt-ms":
                    roundTripDelayMs = ParseNonNegativeInt(args[i], value);
                    break;
                case "--bandwidth-bps":
                    bandwidthBytesPerSecond = ParsePositiveLong(args[i], value);
                    break;
                case "--counters-out":
                    countersPath = value;
                    break;
                case "--miss":
                    missingIds.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                default:
                    throw new ArgumentException($"Unexpected loopback-server argument '{args[i]}'.");
            }
        }

        if (articleCount == 0 || articleBytes == 0 || string.IsNullOrWhiteSpace(countersPath))
            throw new ArgumentException(
                "--nntp-loopback-server requires --articles, --article-bytes, and --counters-out.");
        return new LoopbackServerArguments(
            articleCount,
            articleBytes,
            seed,
            roundTripDelayMs,
            bandwidthBytesPerSecond,
            countersPath,
            missingIds);
    }

    private static int ParsePositiveInt(string name, string value)
    {
        var parsed = ParseInt(name, value);
        if (parsed <= 0)
            throw new ArgumentException($"{name} must be greater than zero.");
        return parsed;
    }

    private static int ParseNonNegativeInt(string name, string value)
    {
        var parsed = ParseInt(name, value);
        if (parsed < 0)
            throw new ArgumentException($"{name} cannot be negative.");
        return parsed;
    }

    private static int ParseInt(string name, string value) =>
        int.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"{name} requires an integer.");

    private static long ParsePositiveLong(string name, string value) =>
        long.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"{name} requires a positive integer.");
}
