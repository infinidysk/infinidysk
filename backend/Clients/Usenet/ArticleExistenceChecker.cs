namespace NzbWebDAV.Clients.Usenet;

internal static class ArticleExistenceChecker
{
    /// <summary>
    /// Checks health/import existence concurrently across the connection pool.
    /// BODY pipelining settings must not collapse these probes onto one connection.
    /// </summary>
    public static Task<IReadOnlyList<int>> CheckAsync(
        INntpClient client,
        IReadOnlyList<string> segmentIds,
        int concurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken,
        int toleratedMissing = 0) =>
        client.CheckAllSegmentsAsync(segmentIds, concurrency, progress, cancellationToken, toleratedMissing);
}
