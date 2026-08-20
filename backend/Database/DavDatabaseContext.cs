using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using Serilog;

namespace NzbWebDAV.Database;

public class DavDatabaseContext : DbContext
{
    public DavDatabaseContext() : base(Options.Value)
    {
    }

    public DavDatabaseContext(DbContextOptions<DavDatabaseContext> options) : base(options)
    {
    }

    protected DavDatabaseContext(DbContextOptions options) : base(options)
    {
    }

    public static string ConfigPath => EnvironmentUtil.GetEnvironmentVariable("CONFIG_PATH") ?? "/config";
    public static string DatabaseFilePath => Path.Join(ConfigPath, "db.sqlite");

    private static Lazy<DbContextOptions<DavDatabaseContext>> Options = CreateOptions();

    internal static void ResetOptionsForTests()
    {
        Options = CreateOptions();
    }

    internal static void ConfigureOptions(DbContextOptionsBuilder options)
    {
        if (DatabaseProviderConfig.IsPostgres)
        {
            options.UseNpgsql(DatabaseProviderConfig.PostgresConnectionString);
            return;
        }

        options
            .UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = DatabaseFilePath,
                DefaultTimeout = 30,
                Pooling = true
            }.ToString())
            .AddInterceptors(new SqliteMainDbPragmas())
            .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>();
    }

    private static Lazy<DbContextOptions<DavDatabaseContext>> CreateOptions() => new(() =>
    {
        var builder = new DbContextOptionsBuilder<DavDatabaseContext>();
        ConfigureOptions(builder);
        return builder.Options;
    });

    // database sets
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<DavItem> Items => Set<DavItem>();
    public DbSet<DavNzbFile> NzbFiles => Set<DavNzbFile>();
    public DbSet<DavRarFile> RarFiles => Set<DavRarFile>();
    public DbSet<DavMultipartFile> MultipartFiles => Set<DavMultipartFile>();
    public DbSet<QueueItem> QueueItems => Set<QueueItem>();
    public DbSet<HistoryItem> HistoryItems => Set<HistoryItem>();
    public DbSet<QueueNzbContents> QueueNzbContents => Set<QueueNzbContents>();
    public DbSet<HealthCheckResult> HealthCheckResults => Set<HealthCheckResult>();
    public DbSet<HealthCheckStat> HealthCheckStats => Set<HealthCheckStat>();
    public DbSet<ConfigItem> ConfigItems => Set<ConfigItem>();
    public DbSet<BlobCleanupItem> BlobCleanupItems => Set<BlobCleanupItem>();
    public DbSet<HistoryCleanupItem> HistoryCleanupItems => Set<HistoryCleanupItem>();
    public DbSet<DavCleanupItem> DavCleanupItems => Set<DavCleanupItem>();
    public DbSet<NzbName> NzbNames => Set<NzbName>();
    public DbSet<NzbBlobCleanupItem> NzbBlobCleanupItems => Set<NzbBlobCleanupItem>();
    public DbSet<WatchdogEntry> WatchdogEntries => Set<WatchdogEntry>();
    public DbSet<IndexerApiHit> IndexerApiHits => Set<IndexerApiHit>();
    public DbSet<ListSource> ListSources => Set<ListSource>();
    public DbSet<WantedItem> WantedItems => Set<WantedItem>();
    public DbSet<NzbResolutionGroup> NzbResolutionGroups => Set<NzbResolutionGroup>();
    public DbSet<ArticleMissCacheEntry> ArticleMissCacheEntries => Set<ArticleMissCacheEntry>();
    public DbSet<Par2RepairJob> Par2RepairJobs => Set<Par2RepairJob>();

    // Pending blob writes for the current unit of work (flushed in SaveChangesAsync).
    private readonly List<DavNzbFile> _blobNzbFiles = [];
    private readonly List<DavRarFile> _blobRarFiles = [];
    private readonly List<DavMultipartFile> _blobMultipartFiles = [];

    public IReadOnlyList<DavNzbFile> BlobNzbFiles => _blobNzbFiles;
    public IReadOnlyList<DavRarFile> BlobRarFiles => _blobRarFiles;
    public IReadOnlyList<DavMultipartFile> BlobMultipartFiles => _blobMultipartFiles;

    public void AddBlob(DavNzbFile file) => _blobNzbFiles.Add(file);
    public void AddBlob(DavRarFile file) => _blobRarFiles.Add(file);
    public void AddBlob(DavMultipartFile file) => _blobMultipartFiles.Add(file);

    public void RemoveNzbBlob(Guid? id)
    {
        if (id is null) return;
        _blobNzbFiles.RemoveAll(x => x.Id == id);
    }

    public void RemoveRarBlob(Guid? id)
    {
        if (id is null) return;
        _blobRarFiles.RemoveAll(x => x.Id == id);
    }

    public void RemoveMultipartBlob(Guid? id)
    {
        if (id is null) return;
        _blobMultipartFiles.RemoveAll(x => x.Id == id);
    }

    public void ClearBlobs()
    {
        _blobNzbFiles.Clear();
        _blobRarFiles.Clear();
        _blobMultipartFiles.Clear();
    }

    // tables
    protected override void OnModelCreating(ModelBuilder b)
    {
        // Account
        b.Entity<Account>(e =>
        {
            e.ToTable("Accounts");
            e.HasKey(i => new { i.Type, i.Username });

            e.Property(i => i.Type)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.Username)
                .IsRequired()
                .HasMaxLength(255);

            e.Property(i => i.PasswordHash)
                .IsRequired();

            e.Property(i => i.RandomSalt)
                .IsRequired();

            // At most one Admin account ever - the frontend's onboarding check-then-act
            // (isOnboarding() then createAccount()) is not itself race-safe, so this is
            // the authoritative backstop against two concurrent onboarding submissions
            // both creating an admin account.
            e.HasIndex(i => i.Type)
                .IsUnique()
                .HasFilter($"\"Type\" = {(int)Account.AccountType.Admin}")
                .HasDatabaseName("IX_Accounts_SingleAdmin");
        });

        // DavItem
        b.Entity<DavItem>(e =>
        {
            e.ToTable("DavItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.Name)
                .IsRequired()
                .HasMaxLength(255);

            e.Property(i => i.Type)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.SubType)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.Path)
                .IsRequired();

            e.Property(i => i.IdPrefix)
                .IsRequired();

            e.Property(i => i.ReleaseDate)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.ToUnixTimeSeconds() : (long?)null,
                    x => x.HasValue ? DateTimeOffset.FromUnixTimeSeconds(x.Value) : null
                );

            e.Property(i => i.LastHealthCheck)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.ToUnixTimeSeconds() : (long?)null,
                    x => x.HasValue ? DateTimeOffset.FromUnixTimeSeconds(x.Value) : null
                );

            e.Property(i => i.NextHealthCheck)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.ToUnixTimeSeconds() : (long?)null,
                    x => x.HasValue ? DateTimeOffset.FromUnixTimeSeconds(x.Value) : null
                );

            e.Property(i => i.FileBlobId)
                .ValueGeneratedNever()
                .IsRequired(false);

            e.Property(i => i.HistoryItemId)
                .ValueGeneratedNever()
                .IsRequired(false);

            e.Property(i => i.NzbBlobId)
                .ValueGeneratedNever()
                .IsRequired(false);

            e.HasIndex(i => new { i.ParentId, i.Name })
                .IsUnique();

            e.HasIndex(i => i.Path)
                .IsUnique();

            e.HasIndex(i => new { i.IdPrefix, i.Type });

            e.HasIndex(i => new { i.Type, i.HistoryItemId, i.NextHealthCheck, i.ReleaseDate, i.Id });

            e.HasIndex(i => new { i.HistoryItemId, i.Type, i.CreatedAt });

            e.HasIndex(i => new { i.HistoryItemId, i.SubType, i.CreatedAt });

            e.HasIndex(i => i.NzbBlobId)
                .IsUnique(false);
        });

        // DavNzbFile
        b.Entity<DavNzbFile>(e =>
        {
            e.ToTable("DavNzbFiles");
            e.HasKey(f => f.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(f => f.SegmentIds)
                .HasConversion(new ValueConverter<string[], string>
                (
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => DeserializeOrFallback<string[]>(v) ?? Array.Empty<string>()
                ))
                .HasColumnType("TEXT") // store raw JSON
                .IsRequired();

            e.HasOne(f => f.DavItem)
                .WithOne()
                .HasForeignKey<DavNzbFile>(f => f.Id)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DavRarFile
        b.Entity<DavRarFile>(e =>
        {
            e.ToTable("DavRarFiles");
            e.HasKey(f => f.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(f => f.RarParts)
                .HasConversion(new ValueConverter<DavRarFile.RarPart[], string>
                (
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => DeserializeOrFallback<DavRarFile.RarPart[]>(v) ?? Array.Empty<DavRarFile.RarPart>()
                ))
                .HasColumnType("TEXT") // store raw JSON
                .IsRequired();

            e.HasOne(f => f.DavItem)
                .WithOne()
                .HasForeignKey<DavRarFile>(f => f.Id)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DavMultipartFile
        b.Entity<DavMultipartFile>(e =>
        {
            e.ToTable("DavMultipartFiles");
            e.HasKey(f => f.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(f => f.Metadata)
                .HasConversion(new ValueConverter<DavMultipartFile.Meta, string>
                (
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => DeserializeOrFallback<DavMultipartFile.Meta>(v) ?? new DavMultipartFile.Meta()
                ))
                .HasColumnType("TEXT") // store raw JSON
                .IsRequired();

            e.HasOne(f => f.DavItem)
                .WithOne()
                .HasForeignKey<DavMultipartFile>(f => f.Id)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // QueueItem
        b.Entity<QueueItem>(e =>
        {
            e.ToTable("QueueItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.SortOrder)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.FileName)
                .IsRequired();

            e.Property(i => i.NzbFileSize)
                .IsRequired();

            e.Property(i => i.TotalSegmentBytes)
                .IsRequired();

            e.Property(i => i.Category)
                .IsRequired();

            e.Property(i => i.Priority)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.PostProcessing)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.PauseUntil)
                .ValueGeneratedNever();

            e.Property(i => i.JobName)
                .IsRequired();

            e.Property(i => i.IndexerName)
                .IsRequired(false);

            e.Property(i => i.ContentGroupKey)
                .IsRequired(false);

            e.HasIndex(i => new { i.Category, i.FileName })
                .IsUnique();

            e.HasIndex(i => new { i.Priority })
                .IsUnique(false);

            e.HasIndex(i => new { i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.Category })
                .IsUnique(false);

            e.HasIndex(i => new { i.Priority, i.SortOrder })
                .IsUnique(false);

            e.HasIndex(i => new { i.Category, i.Priority, i.SortOrder })
                .IsUnique(false);

            e.HasIndex(i => i.ContentGroupKey)
                .IsUnique(false);
        });

        // HistoryItem
        b.Entity<HistoryItem>(e =>
        {
            e.ToTable("HistoryItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.FileName)
                .IsRequired();

            e.Property(i => i.JobName)
                .IsRequired();

            e.Property(i => i.Category)
                .IsRequired();

            e.Property(i => i.DownloadStatus)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.TotalSegmentBytes)
                .IsRequired();

            e.Property(i => i.DownloadTimeSeconds)
                .IsRequired();

            e.Property(i => i.FailMessage)
                .IsRequired(false);

            e.Property(i => i.DownloadDirId)
                .IsRequired(false);

            e.Property(i => i.NzbBlobId)
                .IsRequired(false);

            e.Property(i => i.IndexerName)
                .IsRequired(false);

            e.Property(i => i.ContentGroupKey)
                .IsRequired(false);

            e.Property(i => i.LastPlayedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.ToUnixTimeSeconds() : (long?)null,
                    x => x.HasValue ? DateTimeOffset.FromUnixTimeSeconds(x.Value) : null
                );

            e.HasIndex(i => new { i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.Category })
                .IsUnique(false);

            e.HasIndex(i => new { i.Category, i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.Category, i.DownloadDirId })
                .IsUnique(false);

            e.HasIndex(i => i.NzbBlobId)
                .IsUnique(false);

            e.HasIndex(i => new { i.ContentGroupKey, i.DownloadStatus })
                .IsUnique(false);
        });

        // QueueNzbContents
        b.Entity<QueueNzbContents>(e =>
        {
            e.ToTable("QueueNzbContents");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.NzbContents)
                .IsRequired();

            e.HasOne(f => f.QueueItem)
                .WithOne()
                .HasForeignKey<QueueNzbContents>(f => f.Id)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // HealthCheckResult
        b.Entity<HealthCheckResult>(e =>
        {
            e.ToTable("HealthCheckResults");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .IsRequired()
                .HasConversion(
                    x => x.ToUnixTimeSeconds(),
                    x => DateTimeOffset.FromUnixTimeSeconds(x)
                );

            e.Property(i => i.DavItemId)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.Path)
                .IsRequired();

            e.Property(i => i.NzbFileName)
                .IsRequired(false);

            e.Property(i => i.JobName)
                .IsRequired(false);

            e.Property(i => i.Result)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.RepairStatus)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.Message)
                .IsRequired(false);

            e.HasIndex(i => new { i.Result, i.RepairStatus, i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(h => h.DavItemId)
                .HasFilter("\"RepairStatus\" = 3")
                .IsUnique(false);

            e.HasIndex(i => new { i.RepairStatus, i.CreatedAt })
                .HasFilter("\"RepairStatus\" IN (1, 2)")
                .IsUnique(false);
        });

        // HealthCheckStats
        b.Entity<HealthCheckStat>(e =>
        {
            e.ToTable("HealthCheckStats");
            e.HasKey(i => new { i.DateStartInclusive, i.DateEndExclusive, i.Result, i.RepairStatus });

            e.Property(i => i.DateStartInclusive)
                .ValueGeneratedNever()
                .IsRequired()
                .HasConversion(
                    x => x.ToUnixTimeSeconds(),
                    x => DateTimeOffset.FromUnixTimeSeconds(x)
                );

            e.Property(i => i.DateEndExclusive)
                .ValueGeneratedNever()
                .IsRequired()
                .HasConversion(
                    x => x.ToUnixTimeSeconds(),
                    x => DateTimeOffset.FromUnixTimeSeconds(x)
                );

            e.Property(i => i.Result)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.RepairStatus)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.Count);
        });

        // ConfigItem
        b.Entity<ConfigItem>(e =>
        {
            e.ToTable("ConfigItems");
            e.HasKey(i => i.ConfigName);
            e.Property(i => i.ConfigValue)
                .IsRequired();
        });

        // BlobCleanupItem
        b.Entity<BlobCleanupItem>(e =>
        {
            e.ToTable("BlobCleanupItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();
        });

        // HistoryCleanupItem
        b.Entity<HistoryCleanupItem>(e =>
        {
            e.ToTable("HistoryCleanupItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.DeleteMountedFiles)
                .IsRequired();
        });

        // DavCleanupItem
        b.Entity<DavCleanupItem>(e =>
        {
            e.ToTable("DavCleanupItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();
        });

        // NzbName
        b.Entity<NzbName>(e =>
        {
            e.ToTable("NzbNames");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.FileName)
                .IsRequired();
        });

        // NzbBlobCleanupItem
        b.Entity<NzbBlobCleanupItem>(e =>
        {
            e.ToTable("NzbBlobCleanupItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();
        });

        // WatchdogEntry
        b.Entity<WatchdogEntry>(e =>
        {
            e.ToTable("WatchdogEntries");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedOnAdd();

            e.Property(i => i.ClickId)
                .IsRequired();

            e.Property(i => i.AttemptedAt)
                .IsRequired()
                .HasConversion(
                    x => x.ToUnixTimeSeconds(),
                    x => DateTimeOffset.FromUnixTimeSeconds(x)
                );

            e.Property(i => i.ContentType).IsRequired();
            e.Property(i => i.RequestedTitle).IsRequired();
            e.Property(i => i.CandidateTitle).IsRequired();
            e.Property(i => i.IndexerName).IsRequired();
            e.Property(i => i.Size).IsRequired();
            e.Property(i => i.RankIndex).IsRequired();

            e.Property(i => i.Result)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.FailReason).IsRequired(false);
            e.Property(i => i.DurationMs).IsRequired();
            e.Property(i => i.IsWinner).IsRequired();
            e.Property(i => i.ProviderHost).IsRequired(false);
            e.Property(i => i.QueueItemId).IsRequired(false);
            e.Property(i => i.ContentGroupKey).IsRequired(false);

            e.HasIndex(i => i.AttemptedAt);
            e.HasIndex(i => i.QueueItemId);
            e.HasIndex(i => i.ContentGroupKey);
        });

        // IndexerApiHit
        b.Entity<IndexerApiHit>(e =>
        {
            e.ToTable("IndexerApiHits");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedOnAdd();

            e.Property(i => i.IndexerName)
                .IsRequired();

            e.Property(i => i.Type)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.AccessedAt)
                .IsRequired()
                .HasConversion(
                    x => x.ToUnixTimeSeconds(),
                    x => DateTimeOffset.FromUnixTimeSeconds(x)
                );

            e.HasIndex(i => new { i.IndexerName, i.Type, i.AccessedAt });
            e.HasIndex(i => i.AccessedAt);
        });

        // ListSource
        b.Entity<ListSource>(e =>
        {
            e.ToTable("ListSources");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id).ValueGeneratedNever();
            e.Property(i => i.Kind).IsRequired();
            e.Property(i => i.Name).IsRequired();
            e.Property(i => i.Url).IsRequired(false);
            e.Property(i => i.Enabled).IsRequired();
            e.Property(i => i.Cap).IsRequired();
            e.Property(i => i.CreatedAtUnix).IsRequired();
            e.Property(i => i.LastSyncedAtUnix).IsRequired(false);
            e.Property(i => i.LastSyncError).IsRequired(false);
        });

        // WantedItem
        b.Entity<WantedItem>(e =>
        {
            e.ToTable("WantedItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id).ValueGeneratedNever();
            e.Property(i => i.Key).IsRequired();
            e.Property(i => i.Type).IsRequired();
            e.Property(i => i.ContentId).IsRequired();
            e.Property(i => i.Title).IsRequired();
            e.Property(i => i.State).IsRequired();
            e.Property(i => i.Provenance).IsRequired();
            e.Property(i => i.Shortlist).IsRequired();
            e.Property(i => i.WinnerNzb).IsRequired(false);
            e.Property(i => i.ResponderHost).IsRequired(false);
            e.Property(i => i.FailReason).IsRequired(false);
            e.Property(i => i.CreatedAtUnix).IsRequired();
            e.Property(i => i.UpdatedAtUnix).IsRequired();
            e.Property(i => i.LastResolvedAtUnix).IsRequired(false);
            e.Property(i => i.LastVerifiedAtUnix).IsRequired(false);
            e.Property(i => i.NextCheckAtUnix).IsRequired(false);

            e.HasIndex(i => i.Key).IsUnique();
            e.HasIndex(i => i.NextCheckAtUnix);
            e.HasIndex(i => i.State);
            e.HasIndex(i => i.UpdatedAtUnix);
        });

        // NzbResolutionGroup
        b.Entity<NzbResolutionGroup>(e =>
        {
            e.ToTable("NzbResolutionGroups");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id).ValueGeneratedNever();
            e.Property(i => i.Type).IsRequired();
            e.Property(i => i.ProfileToken).IsRequired();
            e.Property(i => i.SearchId).IsRequired();
            e.Property(i => i.CandidatesJson).IsRequired();
            e.Property(i => i.TokensJson).IsRequired();
            e.Property(i => i.CreatedAtUnix).IsRequired();

            e.HasIndex(i => i.CreatedAtUnix);
        });

        // ArticleMissCacheEntry
        b.Entity<ArticleMissCacheEntry>(e =>
        {
            e.ToTable("ArticleMissCacheEntries");
            e.HasKey(i => i.CacheKey);
            e.Property(i => i.CacheKey).IsRequired();
            e.Property(i => i.ConfirmedAtUnix).IsRequired();
            e.HasIndex(i => i.ConfirmedAtUnix);
        });

        // Par2RepairJob
        b.Entity<Par2RepairJob>(e =>
        {
            e.ToTable("Par2RepairJobs");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.DavItemId)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.Path)
                .IsRequired();

            e.Property(i => i.State)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.MissingSegmentIds)
                .HasConversion(new ValueConverter<string[], string>
                (
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => DeserializeOrFallback<string[]>(v) ?? Array.Empty<string>()
                ))
                .HasColumnType("TEXT")
                .IsRequired();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .IsRequired()
                .HasConversion(
                    x => x.ToUnixTimeSeconds(),
                    x => DateTimeOffset.FromUnixTimeSeconds(x)
                );

            e.Property(i => i.StartedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.ToUnixTimeSeconds() : (long?)null,
                    x => x.HasValue ? DateTimeOffset.FromUnixTimeSeconds(x.Value) : null
                );

            e.Property(i => i.CompletedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.ToUnixTimeSeconds() : (long?)null,
                    x => x.HasValue ? DateTimeOffset.FromUnixTimeSeconds(x.Value) : null
                );

            e.Property(i => i.Attempts)
                .IsRequired();

            e.Property(i => i.NextAttemptAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.ToUnixTimeSeconds() : (long?)null,
                    x => x.HasValue ? DateTimeOffset.FromUnixTimeSeconds(x.Value) : null
                );

            e.Property(i => i.FailureReason)
                .IsRequired(false);

            e.Property(i => i.BytesRead)
                .IsRequired();

            e.Property(i => i.SlicesReconstructed)
                .IsRequired();

            e.HasIndex(i => i.DavItemId);
            e.HasIndex(i => new { i.State, i.NextAttemptAt });
        });

        if (DatabaseProviderConfig.IsPostgres)
        {
            // Existing installs store these values as SQLite date/time text with
            // wall-clock semantics. Mapping to timestamp without time zone preserves
            // that behavior and accepts the local DateTime values created by the app.
            b.Entity<DavItem>().Property(x => x.CreatedAt).HasColumnType("timestamp without time zone");
            b.Entity<QueueItem>().Property(x => x.CreatedAt).HasColumnType("timestamp without time zone");
            b.Entity<QueueItem>().Property(x => x.PauseUntil).HasColumnType("timestamp without time zone");
            b.Entity<HistoryItem>().Property(x => x.CreatedAt).HasColumnType("timestamp without time zone");
        }
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = new CancellationToken())
    {
        try
        {
            // save blobs to blob-store
            foreach (var blobNzbFile in BlobNzbFiles)
                await BlobStore.WriteBlob(blobNzbFile.Id, blobNzbFile).ConfigureAwait(false);
            foreach (var blobRarFile in BlobRarFiles)
                await BlobStore.WriteBlob(blobRarFile.Id, blobRarFile).ConfigureAwait(false);
            foreach (var blobMultipartFile in BlobMultipartFiles)
                await BlobStore.WriteBlob(blobMultipartFile.Id, blobMultipartFile).ConfigureAwait(false);

            // save db changes
            var addedOrRemovedDavItems = GetAddedOrRemovedDavItems();
            var result = await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _ = RcloneVfsForget(addedOrRemovedDavItems, cancellationToken);

            // clear pending blob writes
            ClearBlobs();

            // return
            return result;
        }
        catch
        {
            // on errors, remove any already-written blob files
            foreach (var blobNzbFile in BlobNzbFiles)
                BlobStore.Delete(blobNzbFile.Id);
            foreach (var blobRarFile in BlobRarFiles)
                BlobStore.Delete(blobRarFile.Id);
            foreach (var blobMultipartFile in BlobMultipartFiles)
                BlobStore.Delete(blobMultipartFile.Id);

            // rethrow the exception
            throw;
        }
    }

    private List<DavItem> GetAddedOrRemovedDavItems()
    {
        return ChangeTracker.Entries<DavItem>()
            .Where(x => x.State is EntityState.Added or EntityState.Deleted)
            .Select(x => x.Entity)
            .ToList();
    }

    private static List<string> GetRcloneVfsForgetDirectories(List<DavItem> addedOrRemoved)
    {
        var contentDirs = addedOrRemoved
            .Select(x => x.Path)
            .Select(x => Path.GetDirectoryName(x)!)
            .ToList();

        var idDirs = addedOrRemoved
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .Select(x => DatabaseStoreSymlinkFile.GetTargetPath(x.Id))
            .Select(x => Path.GetDirectoryName(x)!)
            .ToList();

        var completedSymlinkDirs = contentDirs
            .Where(x => x.StartsWith("/content", StringComparison.Ordinal))
            .Select(x => $"/completed-symlinks{x["/content".Length..]}")
            .ToList();

        return contentDirs
            .Concat(completedSymlinkDirs)
            .Concat(idDirs)
            .Distinct()
            .ToList();
    }

    public static async Task RcloneVfsForget(
        List<DavItem> addedOrRemovedDavItems,
        CancellationToken cancellationToken = default)
    {
        var rclone = RcloneClient.Current;
        if (rclone is not { IsRemoteControlEnabled: true, Host: not null }) return;
        if (addedOrRemovedDavItems.Count == 0) return;
        var vfsForgetPaths = GetRcloneVfsForgetDirectories(addedOrRemovedDavItems);
        if (vfsForgetPaths.Count == 0) return;
        await ForgetVfsPathsQuietly(rclone, vfsForgetPaths, cancellationToken).ConfigureAwait(false);
    }

    public static async Task RcloneVfsForget(List<string> paths, CancellationToken cancellationToken = default)
    {
        var rclone = RcloneClient.Current;
        if (rclone is not { IsRemoteControlEnabled: true, Host: not null }) return;
        if (paths.Count == 0) return;
        await ForgetVfsPathsQuietly(rclone, paths, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ForgetVfsPathsQuietly(
        RcloneClient rclone,
        List<string> paths,
        CancellationToken cancellationToken)
    {
        try
        {
            await rclone.ForgetVfsPaths(paths, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Call sites are fire-and-forget; do not surface cancellation as UnobservedTaskException.
        }
    }

    public void ClearChangeTracker()
    {
        ChangeTracker.Clear();
        ClearBlobs();
    }

    /// <summary>
    /// Attempts JSON deserialization; if the column contains non-JSON data
    /// (e.g. legacy Base64+Brotli compressed format), tries to decode that.
    /// Logs the raw value on failure for diagnostics.
    /// Adopted from elfhosted/rebased-v3.
    /// </summary>
    internal static T? DeserializeOrFallback<T>(string? value)
    {
        if (string.IsNullOrEmpty(value)) return default;

        // Fast path: value looks like JSON
        var first = value[0];
        if (first is '[' or '{' or '"' or '-' or (>= '0' and <= '9') or 't' or 'f' or 'n')
        {
            return JsonSerializer.Deserialize<T>(value, (JsonSerializerOptions?)null);
        }

        // Non-JSON data — try Base64+Brotli decode (legacy compressed format)
        Log.Warning(
            "Column contains non-JSON data (starts with '{First}', length={Length}), attempting Base64+Brotli decode",
            first, value.Length);
        try
        {
            var bytes = Convert.FromBase64String(value);
            using var input = new MemoryStream(bytes);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(brotli, Encoding.UTF8);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<T>(json, (JsonSerializerOptions?)null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException or IOException)
        {
            Log.Error(ex, "Failed to deserialize column value. Raw value (first 200 chars): {Preview}",
                value.Length > 200 ? value[..200] : value);
            return default;
        }
    }
}
