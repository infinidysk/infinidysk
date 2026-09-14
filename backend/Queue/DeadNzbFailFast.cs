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

    /// <summary>
    /// Records the missing first segment for step-0 cache and throws a non-retryable failure.
    /// </summary>
    public static void FailMissingImportantFile(NzbFile nzbFile, long generation)
    {
        HealthCheckService.AddMissingSegmentIds([nzbFile.Segments[0].MessageId], generation);

        var fileName = nzbFile.GetSubjectFileName();
        if (string.IsNullOrEmpty(fileName))
            fileName = nzbFile.Subject;

        throw new NonRetryableDownloadException(
            $"Missing articles: 1 important file(s) have missing segments " +
            $"across all providers (e.g. {fileName}). NZB is likely DMCA'd or expired.");
    }
}
