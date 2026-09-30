using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Services;

namespace NzbWebDAV.Queue;

/// <summary>
/// Shared helpers for aborting dead/DMCA'd NZBs when important files are permanently missing.
/// </summary>
public static class DeadNzbFailFast
{
    public static readonly HashSet<string> UnimportantExtensions =
        [".par2", ".nfo", ".txt", ".sfv", ".nzb", ".srr"];

    public static bool IsUnimportantFileName(string fileName) =>
        UnimportantExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    public static bool IsImportantFileName(string fileName) => !IsUnimportantFileName(fileName);

    public static bool IsImportantNzbFile(NzbFile nzbFile) =>
        IsImportantFileName(nzbFile.GetSubjectFileName());

    /// <summary>Key in <see cref="Exception.Data"/> naming the missing file on a fail-fast exception.</summary>
    public const string MissingFileNameDataKey = "NzbWebDAV.MissingFirstSegmentFile";

    private static readonly AsyncLocal<bool> MissingArticleEvidenceSuppressed = new();

    /// <summary>
    /// Stops <see cref="FailMissingImportantFile"/> from recording missing-article evidence in
    /// the shared step-0 cache for the current asynchronous flow. Used by read-only NZB
    /// inspection, which must not change how later imports of the same release behave.
    /// </summary>
    internal static IDisposable SuppressMissingArticleEvidence()
    {
        var previous = MissingArticleEvidenceSuppressed.Value;
        MissingArticleEvidenceSuppressed.Value = true;
        return new EvidenceSuppressionScope(previous);
    }

    /// <summary>
    /// Records the missing first segment for step-0 cache and throws a non-retryable failure.
    /// </summary>
    public static void FailMissingImportantFile(NzbFile nzbFile, long? generation)
    {
        if (generation is { } evidenceGeneration && !MissingArticleEvidenceSuppressed.Value)
            HealthCheckService.AddProviderMissingSegmentIds([nzbFile.Segments[0].MessageId], evidenceGeneration);

        var fileName = nzbFile.GetSubjectFileName();
        if (string.IsNullOrEmpty(fileName))
            fileName = nzbFile.Subject;

        var exception = new NonRetryableDownloadException(
            $"Missing articles: 1 important file(s) have missing segments " +
            $"across all providers (e.g. {fileName}). NZB is likely DMCA'd or expired.");
        exception.Data[MissingFileNameDataKey] = fileName;
        throw exception;
    }

    private sealed class EvidenceSuppressionScope(bool previous) : IDisposable
    {
        public void Dispose() => MissingArticleEvidenceSuppressed.Value = previous;
    }
}
