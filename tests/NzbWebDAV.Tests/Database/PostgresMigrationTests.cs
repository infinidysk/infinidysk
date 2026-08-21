using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Database;

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
                    CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
                    FileName = "queue.nzb",
                    JobName = "queue",
                    Category = "test",
                    Priority = QueueItem.PriorityOption.Normal,
                });
                context.HistoryItems.Add(new HistoryItem
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified),
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
