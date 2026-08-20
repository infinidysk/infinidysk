using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Database;

/// <summary>
/// Dedicated metrics store. Lives in its own SQLite file so it can be vacuumed,
/// backed up, or wiped without touching the operational database. All timestamps
/// are stored as unix milliseconds (INTEGER) so charts can bucket by minute/hour
/// without per-row conversion overhead.
/// </summary>
public sealed class MetricsDbContext : DbContext
{
    public static string DatabaseFilePath => Path.Join(DavDatabaseContext.ConfigPath, "metrics.sqlite");

    private static Lazy<DbContextOptions<MetricsDbContext>> Options = CreateOptions();

    internal static void ResetOptionsForTests()
    {
        Options = CreateOptions();
    }

    private static Lazy<DbContextOptions<MetricsDbContext>> CreateOptions() => new(() =>
        new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite($"Data Source={DatabaseFilePath}")
            .AddInterceptors(new SqliteMetricsPragmas())
            .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options
    );

    public MetricsDbContext() : base(Options.Value)
    {
    }

    internal MetricsDbContext(DbContextOptions<MetricsDbContext> options) : base(options)
    {
    }

    public DbSet<SegmentFetch> SegmentFetches => Set<SegmentFetch>();
    public DbSet<ReadSession> ReadSessions => Set<ReadSession>();
    public DbSet<MetricEvent> MetricEvents => Set<MetricEvent>();
    public DbSet<ThroughputMinute> ThroughputMinutes => Set<ThroughputMinute>();
    public DbSet<ProviderMinute> ProviderMinutes => Set<ProviderMinute>();
    public DbSet<ProviderHourly> ProviderHourly => Set<ProviderHourly>();
    public DbSet<FailoverMiss> FailoverMisses => Set<FailoverMiss>();
    public DbSet<FailoverHourly> FailoverHourly => Set<FailoverHourly>();
    public DbSet<CatalogueDaily> CatalogueDaily => Set<CatalogueDaily>();
    public DbSet<ProviderLifetimeTotal> ProviderLifetimeTotals => Set<ProviderLifetimeTotal>();
    public DbSet<ArrImportEvent> ArrImportEvents => Set<ArrImportEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SegmentFetch>(e =>
        {
            e.ToTable("SegmentFetches");
            e.HasKey(x => x.Id);

            e.Property(x => x.At).IsRequired();
            e.Property(x => x.Provider).IsRequired().HasMaxLength(255);
            e.Property(x => x.Status).HasConversion<int>().IsRequired();
            e.Property(x => x.Bytes).IsRequired();
            e.Property(x => x.DurationMs).IsRequired();
            e.Property(x => x.Retries).IsRequired();

            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.Provider, x.At });
        });

        b.Entity<ReadSession>(e =>
        {
            e.ToTable("ReadSessions");
            e.HasKey(x => x.Id);

            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.StartedAt).IsRequired();
            e.Property(x => x.EndedAt).IsRequired();
            e.Property(x => x.DurationMs).IsRequired();
            e.Property(x => x.Path).IsRequired();
            e.Property(x => x.BytesServed).IsRequired();
            e.Property(x => x.BytesFetched).IsRequired();
            e.Property(x => x.FailoverSaves).IsRequired();
            e.Property(x => x.EndReason).HasConversion<int>().IsRequired();

            e.HasIndex(x => x.StartedAt);
            e.HasIndex(x => x.Path);
        });

        b.Entity<MetricEvent>(e =>
        {
            e.ToTable("MetricEvents");
            e.HasKey(x => x.Id);

            e.Property(x => x.At).IsRequired();
            e.Property(x => x.Kind).IsRequired().HasMaxLength(64);

            e.HasIndex(x => new { x.Kind, x.At });
        });

        b.Entity<ThroughputMinute>(e =>
        {
            e.ToTable("ThroughputMinutes");
            e.HasKey(x => x.Minute);

            e.Property(x => x.Minute).ValueGeneratedNever();
            e.Property(x => x.BytesServed).IsRequired();
            e.Property(x => x.BytesFetched).IsRequired();
            e.Property(x => x.Articles).IsRequired();
            e.Property(x => x.Misses).IsRequired();
            e.Property(x => x.Errors).IsRequired();
            e.Property(x => x.ActiveReadsMax).IsRequired();
        });

        b.Entity<ProviderMinute>(e =>
        {
            e.ToTable("ProviderMinutes");
            e.HasKey(x => new { x.Minute, x.Provider });

            e.Property(x => x.Provider).IsRequired().HasMaxLength(255);
            e.Property(x => x.Articles).IsRequired();
            e.Property(x => x.BytesFetched).IsRequired();
            e.Property(x => x.Misses).IsRequired();
            e.Property(x => x.Errors).IsRequired();
            e.Property(x => x.Retries).IsRequired();
            e.Property(x => x.FailoverSaves).IsRequired();
            e.Property(x => x.SumDurationMs).IsRequired();
        });

        b.Entity<ProviderHourly>(e =>
        {
            e.ToTable("ProviderHourly");
            e.HasKey(x => new { x.Hour, x.Provider });

            e.Property(x => x.Provider).IsRequired().HasMaxLength(255);
            e.Property(x => x.Articles).IsRequired();
            e.Property(x => x.BytesFetched).IsRequired();
            e.Property(x => x.Misses).IsRequired();
            e.Property(x => x.Errors).IsRequired();
            e.Property(x => x.Retries).IsRequired();
            e.Property(x => x.FailoverSaves).IsRequired();
            e.Property(x => x.SumDurationMs).IsRequired();
        });

        b.Entity<FailoverMiss>(e =>
        {
            e.ToTable("FailoverMisses");
            e.HasKey(x => x.Id);

            e.Property(x => x.At).IsRequired();
            e.Property(x => x.FromProvider).IsRequired().HasMaxLength(255);
            e.Property(x => x.ToProvider).IsRequired().HasMaxLength(255);
            e.Property(x => x.Reason).HasConversion<int>().IsRequired();

            e.HasIndex(x => x.At);
        });

        b.Entity<FailoverHourly>(e =>
        {
            e.ToTable("FailoverHourly");
            e.HasKey(x => new { x.Hour, x.FromProvider, x.ToProvider, x.Reason });

            e.Property(x => x.FromProvider).IsRequired().HasMaxLength(255);
            e.Property(x => x.ToProvider).IsRequired().HasMaxLength(255);
            e.Property(x => x.Reason).HasConversion<int>().IsRequired();
            e.Property(x => x.Count).IsRequired();
        });

        b.Entity<CatalogueDaily>(e =>
        {
            e.ToTable("CatalogueDaily");
            e.HasKey(x => x.Day);

            e.Property(x => x.Day).ValueGeneratedNever();
            e.Property(x => x.FileCount).IsRequired();
            e.Property(x => x.TotalBytes).IsRequired();
            e.Property(x => x.AddedCount).IsRequired();
            e.Property(x => x.RemovedCount).IsRequired();
        });
        b.Entity<ProviderLifetimeTotal>(e =>
        {
            e.ToTable("ProviderLifetimeTotals");
            e.HasKey(x => x.Provider);

            e.Property(x => x.Provider).IsRequired().HasMaxLength(255);
            e.Property(x => x.BytesFetched).IsRequired();
            e.Property(x => x.Articles).IsRequired();
            e.Property(x => x.Misses).IsRequired();
            e.Property(x => x.Errors).IsRequired();
            e.Property(x => x.Retries).IsRequired();
            e.Property(x => x.SumDurationMs).IsRequired();
            e.Property(x => x.FailoverSaves).IsRequired();
        });

        b.Entity<ArrImportEvent>(e =>
        {
            e.ToTable("ArrImportEvents");
            e.HasKey(x => x.Id);

            e.Property(x => x.InstanceKey).IsRequired().HasMaxLength(512);
            e.Property(x => x.ArrRecordId).IsRequired();
            e.Property(x => x.DownloadId).IsRequired();
            e.Property(x => x.ImportedAtMs).IsRequired();

            e.HasIndex(x => new { x.InstanceKey, x.ArrRecordId }).IsUnique();
            e.HasIndex(x => x.ImportedAtMs);
            e.HasIndex(x => new { x.InstanceKey, x.ImportedAtMs });
        });
    }
}
