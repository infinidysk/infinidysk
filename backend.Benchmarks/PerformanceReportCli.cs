namespace NzbWebDAV.Benchmarks;

internal static class PerformanceReportCli
{
    public static async Task<bool> TryHandleAsync(string[] args)
    {
        if (args.Length == 0)
            return false;

        string? report = null;
        string? jsonPath = null;
        for (var i = 0; i < args.Length;)
        {
            var arg = args[i];
            if (arg is "--streaming-report" or "--sab-api-report")
            {
                if (report is not null)
                    throw new ArgumentException("Multiple performance reports in one invocation.");
                report = arg == "--streaming-report" ? "streaming" : "sab-api";
                i++;
                continue;
            }

            if (arg == "--json")
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException("--json requires a file path.");
                jsonPath = args[i + 1];
                i += 2;
                continue;
            }

            if (report is not null || jsonPath is not null)
                throw new ArgumentException($"Unexpected argument '{arg}'.");
            return false;
        }

        if (report is null)
        {
            if (jsonPath is not null)
                throw new ArgumentException("--json requires --streaming-report or --sab-api-report.");
            return false;
        }

        if (report == "streaming")
        {
            await RepeatableStreamingReport.RunAsync(jsonPath).ConfigureAwait(false);
            return true;
        }

        if (jsonPath is null)
            throw new ArgumentException("--sab-api-report requires --json <path>.");
        await SabApiReport.RunAsync(jsonPath).ConfigureAwait(false);
        return true;
    }
}
