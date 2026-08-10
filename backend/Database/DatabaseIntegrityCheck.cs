using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Database;

/// <summary>
/// Verifies the main database file with PRAGMA quick_check after startup migrations,
/// so corruption surfaces as one clear log event with recovery guidance instead of
/// scattered "database disk image is malformed" failures across background services.
/// quick_check (not integrity_check) keeps startup fast on large databases while
/// still detecting the malformed-image class of corruption. The metrics database is
/// deliberately not checked: it is rebuildable and disposable.
/// </summary>
internal static class DatabaseIntegrityCheck
{
    private const int MaxLoggedFindings = 10;

    /// <summary>
    /// Returns false when corruption was detected. Never throws and never blocks
    /// startup: the guided restore flow (Settings → Backup &amp; Restore) requires a
    /// running backend, so crash-looping here would remove the operator's only
    /// in-app recovery path.
    /// </summary>
    public static async Task<bool> VerifyMainDatabaseAsync(
        DavDatabaseContext databaseContext,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Table-valued pragma form so EF can materialize the scalar result.
            var findings = await databaseContext.Database
                .SqlQueryRaw<string>("SELECT quick_check AS Value FROM pragma_quick_check")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // quick_check returns a single "ok" row when the database is healthy.
            var problems = findings
                .Where(x => !string.Equals(x, "ok", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (problems.Count == 0)
                return true;

            LogCorruption(problems);
            return false;
        }
        catch (Exception ex) when (ex.IsDatabaseCorruptionException())
        {
            // The file is so damaged that quick_check itself could not run.
            LogCorruption([ex.Message]);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Startup is shutting down; a cancelled check is not a corruption finding.
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A diagnostic must never break startup.
            Log.Warning(ex, "Database integrity check could not run; continuing startup.");
            return true;
        }
    }

    private static void LogCorruption(List<string> findings)
    {
        Log.Error("Database integrity check failed. {Reason}", ExceptionExtensions.DatabaseCorruptionReason);
        foreach (var finding in findings.Take(MaxLoggedFindings))
            Log.Error("Database integrity check finding: {Finding}", finding);
        if (findings.Count > MaxLoggedFindings)
        {
            Log.Error(
                "Database integrity check reported {Count} findings (showing first {Shown})",
                findings.Count,
                MaxLoggedFindings);
        }
    }
}
