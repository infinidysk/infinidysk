using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NzbWebDAV.Api.Controllers.GetOverviewStats;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Database;

[Collection(nameof(DatabaseContractWriterCollection))]
public sealed class PostgresMigrationTests
{
    [SkippableFact]
    public async Task MigrateAsync_AppliesFreshPostgresSchema()
    {
        Skip.IfNot(
            DatabaseProviderConfig.IsPostgres,
            "PostgreSQL migration tests require DATABASE_PROVIDER=postgres.");

        var schema = $"nzbdav_test_{Guid.NewGuid():N}";
        var connectionString = DatabaseProviderConfig.PostgresConnectionString;

        await using var adminConnection = new NpgsqlConnection(connectionString);
        await adminConnection.OpenAsync();
        await ExecuteAsync(adminConnection, $"CREATE SCHEMA \"{schema}\"");

        try
        {
            var scopedConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = schema
            }.ConnectionString;
            var options = new DbContextOptionsBuilder<PostgresDavDatabaseContext>()
                .UseNpgsql(scopedConnectionString)
                .Options;

            await using var context = new PostgresDavDatabaseContext(options);
            await context.Database.MigrateAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.True(await DatabaseStartupGuards.ConfigItemsTableExistsAsync(context));
            Assert.Equal(5, await context.Items.CountAsync());

            Assert.Equal("uuid", await GetColumnTypeAsync(context, "DavItems", "ArrDownloadId"));
            Assert.Equal("YES", await GetIsNullableAsync(context, "DavItems", "ArrDownloadId"));
            Assert.Equal("uuid", await GetColumnTypeAsync(context, "HistoryItems", "ArrDownloadId"));
            Assert.Equal("YES", await GetIsNullableAsync(context, "HistoryItems", "ArrDownloadId"));
            Assert.Equal("uuid", await GetColumnTypeAsync(context, "QueueItems", "ArrDownloadId"));
            Assert.Equal("YES", await GetIsNullableAsync(context, "QueueItems", "ArrDownloadId"));

            var id = Guid.Parse("AbCdEf01-2345-4aBc-8DeF-0123456789Ab");
            context.QueueItems.Add(new QueueItem
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                FileName = "pg-provenance.nzb",
                JobName = "pg-provenance",
                NzbFileSize = 1,
                TotalSegmentBytes = 1,
                Category = "tv",
                Priority = QueueItem.PriorityOption.Normal,
                PostProcessing = QueueItem.PostProcessingOption.None,
                ArrDownloadId = id,
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            Assert.Equal(
                id,
                (await context.QueueItems.AsNoTracking().SingleAsync(x => x.FileName == "pg-provenance.nzb"))
                    .ArrDownloadId);
        }
        finally
        {
            await ExecuteAsync(adminConnection, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
    }

    [SkippableFact]
    public async Task RemoveEmptyDirectoriesAsync_AcceptsLocalTimestampCutoff()
    {
        Skip.IfNot(
            DatabaseProviderConfig.IsPostgres,
            "PostgreSQL migration tests require DATABASE_PROVIDER=postgres.");

        var schema = $"nzbdav_orphan_cleanup_{Guid.NewGuid():N}";
        var connectionString = DatabaseProviderConfig.PostgresConnectionString;
        await using var adminConnection = new NpgsqlConnection(connectionString);
        await adminConnection.OpenAsync();
        await ExecuteAsync(adminConnection, $"CREATE SCHEMA \"{schema}\"");

        try
        {
            var scopedConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = schema
            }.ConnectionString;
            var options = new DbContextOptionsBuilder<PostgresDavDatabaseContext>()
                .UseNpgsql(scopedConnectionString)
                .Options;
            await using var context = new PostgresDavDatabaseContext(options);
            await context.Database.MigrateAsync();

            var id = Guid.NewGuid();
            var cutoff = new DateTime(2026, 8, 23, 12, 34, 56, DateTimeKind.Local);
            context.Items.Add(new DavItem
            {
                Id = id,
                IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
                CreatedAt = cutoff.AddMinutes(-1),
                ParentId = DavItem.Root.Id,
                Name = "orphaned-directory",
                Type = DavItem.ItemType.Directory,
                SubType = DavItem.ItemSubType.Directory,
                Path = "/orphaned-directory",
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var removed = await RemoveUnlinkedFilesTask.RemoveEmptyDirectoriesAsync(
                context, cutoff);

            Assert.Equal(1, removed);
            Assert.False(await context.Items.AsNoTracking().AnyAsync(x => x.Id == id));
        }
        finally
        {
            await ExecuteAsync(adminConnection, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
    }

    [SkippableFact]
    public async Task SabQueue_CountAndPageQueriesAreSequenced()
    {
        Skip.IfNot(DatabaseProviderConfig.IsPostgres,
            "PostgreSQL tests require DATABASE_PROVIDER=postgres.");

        await using var fixture = await SabControllerFixture.CreateAsync();
        var commandStarted = fixture.Interceptor.DelayNextCommand();
        var responseTask = fixture.QueueController.GetQueueAsync(fixture.CreateQueueRequest());

        try
        {
            await commandStarted.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            fixture.Interceptor.ReleaseDelayedCommand();
        }

        var response = await responseTask;
        Assert.Single(response.Queue.Slots);
        Assert.Equal(1, response.Queue.TotalCount);
    }

    [SkippableFact]
    public async Task SabHistory_CountAndPageQueriesAreSequenced()
    {
        Skip.IfNot(DatabaseProviderConfig.IsPostgres,
            "PostgreSQL tests require DATABASE_PROVIDER=postgres.");

        await using var fixture = await SabControllerFixture.CreateAsync();
        var commandStarted = fixture.Interceptor.DelayNextCommand();
        var responseTask = fixture.HistoryController.GetHistoryAsync(fixture.CreateHistoryRequest());

        try
        {
            await commandStarted.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            fixture.Interceptor.ReleaseDelayedCommand();
        }

        var response = await responseTask;
        Assert.Single(response.History.Slots);
        Assert.Equal(1, response.History.TotalCount);
    }

    [SkippableFact]
    public async Task WallClockTimestamps_AcceptUtcValuesAndAgeQueries()
    {
        Skip.IfNot(DatabaseProviderConfig.IsPostgres,
            "PostgreSQL tests require DATABASE_PROVIDER=postgres.");

        var schema = $"nzbdav_wall_clock_{Guid.NewGuid():N}";
        var connectionString = DatabaseProviderConfig.PostgresConnectionString;
        await using var adminConnection = new NpgsqlConnection(connectionString);
        await adminConnection.OpenAsync();
        await ExecuteAsync(adminConnection, $"CREATE SCHEMA \"{schema}\"");

        try
        {
            var scopedConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = schema
            }.ConnectionString;
            var options = new DbContextOptionsBuilder<PostgresDavDatabaseContext>()
                .UseNpgsql(scopedConnectionString)
                .Options;
            await using var context = new PostgresDavDatabaseContext(options);
            await context.Database.MigrateAsync();

            var utcCreatedAt = DateTime.UtcNow;
            var oldHistoryId = Guid.NewGuid();
            context.HistoryItems.AddRange(
                CreateHistory(oldHistoryId, "retention", DateTime.Now.AddDays(-100)),
                CreateHistory(Guid.NewGuid(), "indexer", DateTime.Now.AddDays(-20), indexerName: "Example"),
                CreateHistory(Guid.NewGuid(), "utc", utcCreatedAt));
            context.Items.AddRange(
                CreateItem("old-file", DateTime.Now.AddDays(-8)),
                CreateItem("recent-file", DateTime.Now.AddDays(-2)));
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var utcRoundTrip = await context.HistoryItems.SingleAsync(x => x.JobName == "utc");
            var expectedUtcRoundTrip = DateTime.SpecifyKind(
                utcCreatedAt.ToLocalTime(), DateTimeKind.Unspecified);
            Assert.Equal(DateTimeKind.Unspecified, utcRoundTrip.CreatedAt.Kind);
            Assert.Equal(
                expectedUtcRoundTrip.Ticks - (expectedUtcRoundTrip.Ticks % 10),
                utcRoundTrip.CreatedAt.Ticks);

            var matchingUtcRows = await context.HistoryItems
                .Where(x => x.CreatedAt <= utcCreatedAt)
                .CountAsync();
            Assert.Equal(3, matchingUtcRows);

            var catalogue = await GetOverviewStatsController.BuildCatalogueAsync(context);
            Assert.Equal(2, catalogue.FileCount);
            Assert.Equal(1, catalogue.AddedLast7Days);

            var indexers = await GetOverviewStatsController.BuildIndexersAsync(context);
            Assert.Equal("Example", Assert.Single(indexers).Name);

            var dbClient = new DavDatabaseClient(context);
            Assert.Equal(1, await HistoryRetentionService.SweepAsync(dbClient, 90, CancellationToken.None));
            Assert.Null(await context.HistoryItems.FindAsync(oldHistoryId));

            context.HistoryItems.Add(CreateHistory(Guid.NewGuid(), "prune", DateTime.Now.AddDays(-100),
                category: "prune"));
            await context.SaveChangesAsync();

            Assert.Equal(1, await PruneCompletedHistoryTask.BuildFilterQuery(context, "prune", 90)
                .CountAsync());
        }
        finally
        {
            await ExecuteAsync(adminConnection, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        }
    }

    [SkippableFact]
    public async Task DatabaseContractWriter_ReadsMainHistoryFromPostgres()
    {
        Skip.IfNot(
            DatabaseProviderConfig.IsPostgres,
            "PostgreSQL migration tests require DATABASE_PROVIDER=postgres.");

        // The default factory must route to the PostgreSQL migration context.
        await using (var probe = DatabaseContractWriter.MainContextFactory())
            Assert.IsType<PostgresDavDatabaseContext>(probe);

        var schema = $"nzbdav_contract_{Guid.NewGuid():N}";
        var connectionString = DatabaseProviderConfig.PostgresConnectionString;
        await using var adminConnection = new NpgsqlConnection(connectionString);
        await adminConnection.OpenAsync();
        await ExecuteAsync(adminConnection, $"CREATE SCHEMA \"{schema}\"");

        var configRoot = Path.Join(Path.GetTempPath(), $"nzbdav-contract-pg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configRoot);
        var contractPath = Path.Join(configRoot, "db-contract.json");

        var previousMainFactory = DatabaseContractWriter.MainContextFactory;
        var previousMetricsFactory = DatabaseContractWriter.MetricsContextFactory;
        var previousLedgerFilePath = DatabaseContractWriter.UsenetMigrationDatabaseFilePath;
        var previousContractFilePath = DatabaseContractWriter.ContractFilePath;

        try
        {
            var scopedConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = schema
            }.ConnectionString;
            var mainOptions = new DbContextOptionsBuilder<PostgresDavDatabaseContext>()
                .UseNpgsql(scopedConnectionString)
                .Options;

            List<string> applied;
            await using (var context = new PostgresDavDatabaseContext(mainOptions))
            {
                await context.Database.MigrateAsync().WaitAsync(TimeSpan.FromSeconds(30));
                applied = (await context.Database.GetAppliedMigrationsAsync())
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
            }

            var metricsOptions = new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite($"Data Source={Path.Join(configRoot, "metrics.sqlite")};Pooling=False")
                .AddInterceptors(new SqliteMetricsPragmas())
                .ReplaceService<
                    IMigrationsSqlGenerator,
                    SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
                .Options;
            await using (var metrics = new MetricsDbContext(metricsOptions))
                await metrics.Database.MigrateAsync();

            DatabaseContractWriter.MainContextFactory = () => new PostgresDavDatabaseContext(mainOptions);
            DatabaseContractWriter.MetricsContextFactory = () => new MetricsDbContext(metricsOptions);
            DatabaseContractWriter.UsenetMigrationDatabaseFilePath =
                () => Path.Join(configRoot, "usenet-migration.db");
            DatabaseContractWriter.ContractFilePath = () => contractPath;

            await DatabaseContractWriter.WriteAsync();

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(contractPath));
            var root = doc.RootElement;
            Assert.Equal("postgres", root.GetProperty("provider").GetString());
            Assert.Equal(applied.Last(), root.GetProperty("terminalMigration").GetString());
            Assert.Equal(applied.Count, root.GetProperty("migrationCount").GetInt32());

            var main = root.GetProperty("databases").GetProperty("main");
            Assert.Equal("postgres", main.GetProperty("provider").GetString());
            Assert.Equal(applied.Last(), main.GetProperty("terminalMigration").GetString());
            Assert.Equal(applied.Count, main.GetProperty("migrationCount").GetInt32());
        }
        finally
        {
            DatabaseContractWriter.MainContextFactory = previousMainFactory;
            DatabaseContractWriter.MetricsContextFactory = previousMetricsFactory;
            DatabaseContractWriter.UsenetMigrationDatabaseFilePath = previousLedgerFilePath;
            DatabaseContractWriter.ContractFilePath = previousContractFilePath;
            await ExecuteAsync(adminConnection, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            try
            {
                Directory.Delete(configRoot, recursive: true);
            }
            catch (IOException)
            {
                // best effort — temp files.
            }
        }
    }

    private static DavItem CreateItem(string name, DateTime createdAt)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = createdAt,
            ParentId = DavItem.Root.Id,
            Name = name,
            FileSize = 42,
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = $"/content/{name}",
        };
    }

    private static HistoryItem CreateHistory(
        Guid id,
        string jobName,
        DateTime createdAt,
        string category = "test",
        string? indexerName = null) => new()
        {
            Id = id,
            CreatedAt = createdAt,
            FileName = $"{jobName}.nzb",
            JobName = jobName,
            Category = category,
            IndexerName = indexerName,
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
        };

    private static async Task<string> GetColumnTypeAsync(
        PostgresDavDatabaseContext context, string table, string column)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync();
        command.CommandText =
            """
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name = @table
              AND column_name = @column;
            """;
        AddParameter(command, "@table", table);
        AddParameter(command, "@column", column);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<string> GetIsNullableAsync(
        PostgresDavDatabaseContext context, string table, string column)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync();
        command.CommandText =
            """
            SELECT is_nullable
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name = @table
              AND column_name = @column;
            """;
        AddParameter(command, "@table", table);
        AddParameter(command, "@column", column);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string commandText)
    {
        await using var command = new NpgsqlCommand(commandText, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class SabControllerFixture : IAsyncDisposable
    {
        private readonly string _schema;
        private readonly NpgsqlConnection _adminConnection;
        private readonly PostgresDavDatabaseContext _context;
        private readonly QueueManager _queueManager;
        private readonly ConfigManager _configManager;
        private readonly DavDatabaseClient _dbClient;
        public DelayingDbCommandInterceptor Interceptor { get; }

        private SabControllerFixture(
            string schema,
            NpgsqlConnection adminConnection,
            PostgresDavDatabaseContext context,
            QueueManager queueManager,
            ConfigManager configManager,
            DavDatabaseClient dbClient,
            DelayingDbCommandInterceptor interceptor)
        {
            _schema = schema;
            _adminConnection = adminConnection;
            _context = context;
            _queueManager = queueManager;
            _configManager = configManager;
            _dbClient = dbClient;
            Interceptor = interceptor;
        }

        public GetQueueController QueueController =>
            new(new DefaultHttpContext(), _dbClient, _queueManager, _configManager, new ProviderUsageTracker());

        public GetHistoryController HistoryController =>
            new(new DefaultHttpContext(), _dbClient, _configManager, new ProviderUsageTracker());

        public GetQueueRequest CreateQueueRequest() =>
            new(new DefaultHttpContext(), _configManager);

        public GetHistoryRequest CreateHistoryRequest() =>
            new(new DefaultHttpContext(), _configManager);

        public static async Task<SabControllerFixture> CreateAsync()
        {
            var schema = $"nzbdav_sab_test_{Guid.NewGuid():N}";
            var adminConnection = new NpgsqlConnection(DatabaseProviderConfig.PostgresConnectionString);
            await adminConnection.OpenAsync();
            await ExecuteAsync(adminConnection, $"CREATE SCHEMA \"{schema}\"");

            try
            {
                var connectionString = new NpgsqlConnectionStringBuilder(
                    DatabaseProviderConfig.PostgresConnectionString)
                { SearchPath = schema }.ConnectionString;
                var interceptor = new DelayingDbCommandInterceptor();
                var options = new DbContextOptionsBuilder<PostgresDavDatabaseContext>()
                    .UseNpgsql(connectionString)
                    .AddInterceptors(interceptor)
                    .Options;
                var context = new PostgresDavDatabaseContext(options);
                await context.Database.MigrateAsync();

                context.QueueItems.Add(new QueueItem
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = DateTime.Now,
                    FileName = "queue.nzb",
                    JobName = "queue",
                    Category = "test",
                    Priority = QueueItem.PriorityOption.Normal,
                });
                context.HistoryItems.Add(new HistoryItem
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = DateTime.Now,
                    FileName = "history.nzb",
                    JobName = "history",
                    Category = "test",
                    DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                });
                await context.SaveChangesAsync();
                context.ChangeTracker.Clear();

                var configManager = new ConfigManager();
                configManager.UpdateValues([
                    new ConfigItem
                    {
                        ConfigName = ConfigKeys.UsenetProviders,
                        ConfigValue = "{\"providers\":[]}",
                    },
                ]);
                var websocketManager = new WebsocketManager();
                var queueManager = new QueueManager(
                    new UsenetStreamingClient(
                        configManager, websocketManager, new ProviderUsageTracker(), new MetricsWriter(),
                        new ProviderBytesTracker(), new StreamTraceBuffer(100), new ActiveReadRegistry()),
                    configManager, websocketManager, new ProviderUsageTracker(), new WatchdogLog(),
                    new QueueItemSourceTracker(), new BenchmarkGate(), startLoop: false);
                return new SabControllerFixture(schema, adminConnection, context, queueManager,
                    configManager, new DavDatabaseClient(context), interceptor);
            }
            catch
            {
                await ExecuteAsync(adminConnection, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await adminConnection.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _queueManager.Dispose();
            await _context.DisposeAsync();
            await ExecuteAsync(_adminConnection, $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE");
            await _adminConnection.DisposeAsync();
        }
    }

    private sealed class DelayingDbCommandInterceptor : DbCommandInterceptor
    {
        private TaskCompletionSource? _commandStarted;
        private TaskCompletionSource? _releaseCommand;

        public Task DelayNextCommand()
        {
            _commandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _releaseCommand = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _commandStarted.Task;
        }

        public void ReleaseDelayedCommand() => _releaseCommand?.TrySetResult();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            DelayAsync(command, eventData, result, cancellationToken);

        private async ValueTask<InterceptionResult<DbDataReader>> DelayAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken)
        {
            if (_releaseCommand is { } releaseCommand)
            {
                _commandStarted?.TrySetResult();
                await releaseCommand.Task.ConfigureAwait(false);
                _releaseCommand = null;
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
