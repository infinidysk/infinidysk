using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Database;

/// <summary>
/// Writes a machine-readable database contract (<c>db-contract.json</c>) to CONFIG_PATH
/// after migration events, so external migrators (e.g. DUMB's SQLite-to-PostgreSQL
/// migrator) can pin against the exact applied migration history instead of
/// reverse-engineering schema identity from __EFMigrationsHistory, backup manifests,
/// or the app version. The top-level fields describe the main database; the
/// <c>databases</c> map carries every database the runtime owns.
/// </summary>
internal static class DatabaseContractWriter
{
    internal const string ContractVersion = "infinidysk-db-v1";
    internal const string ContractFileName = "db-contract.json";

    // Runtime tables that are not part of the stable schema. RemoveUnlinkedFilesTask
    // recreates TMP_LINKED_FILES on every orphan-cleanup pass, and TMP_LINKED_FILES_UNIQUE
    // can be stranded when a run dies between CREATE and RENAME.
    private static readonly string[] MainTransientObjects = ["TMP_LINKED_FILES", "TMP_LINKED_FILES_UNIQUE"];

    // Test seams (same pattern as UsenetMigrationStore).
    internal static Func<DavDatabaseContext> MainContextFactory { get; set; } =
        static () => DatabaseProviderConfig.IsPostgres
            ? new PostgresDavDatabaseContext()
            : new DavDatabaseContext();

    internal static Func<MetricsDbContext> MetricsContextFactory { get; set; } =
        static () => new MetricsDbContext();

    internal static Func<UsenetMigrationDbContext> UsenetMigrationContextFactory { get; set; } =
        static () => new UsenetMigrationDbContext();

    internal static Func<string> UsenetMigrationDatabaseFilePath { get; set; } =
        static () => UsenetMigrationDbContext.DatabaseFilePath;

    internal static Func<string> ContractFilePath { get; set; } =
        static () => Path.Join(DavDatabaseContext.ConfigPath, ContractFileName);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Writes the contract file. Never throws: the contract is informational and must
    /// not block startup or the usenet-migration wizard.
    /// </summary>
    public static async Task WriteAsync(CancellationToken ct = default)
    {
        try
        {
            await WriteCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warning("Could not write database contract file to {Path}: {Reason}", ContractFilePath(), e.Message);
            Log.Debug(e, "Database contract write failure stack");
        }
    }

    private static async Task WriteCoreAsync(CancellationToken ct)
    {
        var provider = DatabaseProviderConfig.IsPostgres ? "postgres" : "sqlite";

        DatabaseContractEntry main;
        await using (var context = MainContextFactory())
            main = await ReadEntryAsync(context, provider, MainTransientObjects, ct).ConfigureAwait(false);

        DatabaseContractEntry metrics;
        await using (var context = MetricsContextFactory())
            metrics = await ReadEntryAsync(context, "sqlite", [], ct).ConfigureAwait(false);

        // The usenet-migration ledger is created lazily by UsenetMigrationStore, which
        // treats file existence as meaningful state — never create it as a side effect.
        var usenetMigration = new DatabaseContractEntry("sqlite", null, 0, null, []);
        if (File.Exists(UsenetMigrationDatabaseFilePath()))
        {
            await using var context = UsenetMigrationContextFactory();
            usenetMigration = await ReadEntryAsync(context, "sqlite", [], ct).ConfigureAwait(false);
        }

        var contract = new DatabaseContract(
            ContractVersion,
            ConfigManager.AppVersion,
            DateTime.UtcNow.ToString("O"),
            main.Provider,
            main.TerminalMigration,
            main.MigrationCount,
            main.MigrationHistoryHash,
            main.TransientObjects,
            new Dictionary<string, DatabaseContractEntry>
            {
                ["main"] = main,
                ["metrics"] = metrics,
                ["usenetMigration"] = usenetMigration,
            });

        WriteFileAtomically(ContractFilePath(), JsonSerializer.Serialize(contract, SerializerOptions));
    }

    private static async Task<DatabaseContractEntry> ReadEntryAsync(
        DbContext context,
        string provider,
        string[] transientObjects,
        CancellationToken ct)
    {
        var applied = await context.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false);
        var sorted = applied.OrderBy(id => id, StringComparer.Ordinal).ToList();
        return new DatabaseContractEntry(
            provider,
            sorted.LastOrDefault(),
            sorted.Count,
            ComputeHistoryHash(sorted),
            transientObjects);
    }

    private static string ComputeHistoryHash(IReadOnlyCollection<string> sortedMigrationIds)
    {
        var payload = string.Join('\n', sortedMigrationIds);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static void WriteFileAtomically(string finalPath, string json)
    {
        var directory = Path.GetDirectoryName(finalPath)!;

        foreach (var stale in Directory.EnumerateFiles(directory, "db-contract.*.tmp"))
        {
            try
            {
                File.Delete(stale);
            }
            catch (IOException)
            {
                // best effort — stale temp files from a crashed write.
            }
            catch (UnauthorizedAccessException)
            {
                // best effort — stale temp files from a crashed write.
            }
        }

        // Replace-by-rename only needs write permission on the directory, so a
        // root-owned db-contract.json left by a previous install can never block an
        // upgrade or reinstall. The temp file is chmodded before the rename so the
        // contract is world-readable (DUMB may read it as another user) from the
        // moment it appears at the final path.
        var tempPath = Path.Join(directory, $"db-contract.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, json, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        File.Move(tempPath, finalPath, overwrite: true);
    }

    private sealed record DatabaseContractEntry(
        string Provider,
        string? TerminalMigration,
        int MigrationCount,
        string? MigrationHistoryHash,
        string[] TransientObjects);

    private sealed record DatabaseContract(
        string Contract,
        string AppVersion,
        string GeneratedAtUtc,
        string Provider,
        string? TerminalMigration,
        int MigrationCount,
        string? MigrationHistoryHash,
        string[] TransientObjects,
        Dictionary<string, DatabaseContractEntry> Databases);
}
