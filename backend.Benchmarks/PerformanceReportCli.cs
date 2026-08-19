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
            if (arg == "--streaming-report")
            {
                if (report is not null)
                    throw new ArgumentException("Multiple performance reports in one invocation.");
                report = "streaming";
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
                throw new ArgumentException("--json requires --streaming-report.");
            return false;
        }

        await RepeatableStreamingReport.RunAsync(jsonPath).ConfigureAwait(false);
        return true;
    }
}
