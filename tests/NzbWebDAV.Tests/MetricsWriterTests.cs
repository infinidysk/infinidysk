using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Tests;

public class MetricsWriterTests
{
    [Fact]
    public async Task FailedFlush_RequeuesMetricsAndReportsError()
    {
        var invalidParent = Path.GetTempFileName();
        try
        {
            var options = new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite($"Data Source={Path.Join(invalidParent, "metrics.sqlite")}")
                .Options;
            var writer = new MetricsWriter(() => new MetricsDbContext(options));
            writer.RecordFetch(new SegmentFetch
            {
                At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Provider = "test",
                Status = SegmentFetch.FetchStatus.Ok,
            });

            await Assert.ThrowsAnyAsync<Exception>(() => writer.FlushNowAsync());

            Assert.Equal(1, writer.Stats.QueuedFetches);
            Assert.NotNull(writer.Stats.LastFlushError);
            Assert.Equal(1, writer.ConsecutiveFlushFailures);

            // A second failure keeps counting; the loop's backoff grows with it.
            await Assert.ThrowsAnyAsync<Exception>(() => writer.FlushNowAsync());
            Assert.Equal(2, writer.ConsecutiveFlushFailures);
        }
        finally
        {
            File.Delete(invalidParent);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 8)]
    [InlineData(7, 60)]
    [InlineData(40, 60)]
    public void BackoffFor_DoublesAndCapsAtOneMinute(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), MetricsWriter.BackoffFor(failures));
    }

    [Fact]
    public async Task SuccessfulFlush_ResetsFailureCount()
    {
        var databasePath = Path.Join(Path.GetTempPath(), $"nzbdav-metrics-writer-{Guid.NewGuid():N}.sqlite");
        var invalidParent = Path.GetTempFileName();
        try
        {
            var invalidOptions = new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite($"Data Source={Path.Join(invalidParent, "metrics.sqlite")}")
                .Options;
            var validOptions = new DbContextOptionsBuilder<MetricsDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            await using (var context = new MetricsDbContext(validOptions))
                await context.Database.MigrateAsync();

            Func<MetricsDbContext> contextFactory = () => new MetricsDbContext(invalidOptions);
            var writer = new MetricsWriter(() => contextFactory());
            writer.RecordFetch(new SegmentFetch
            {
                At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Provider = "test",
                Status = SegmentFetch.FetchStatus.Ok,
            });
            await Assert.ThrowsAnyAsync<Exception>(() => writer.FlushNowAsync());
            Assert.NotEqual(0, writer.ConsecutiveFlushFailures);

            contextFactory = () => new MetricsDbContext(validOptions);
            await writer.FlushNowAsync();
            Assert.Equal(0, writer.ConsecutiveFlushFailures);
            Assert.Null(writer.Stats.LastFlushError);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
                if (File.Exists(f)) File.Delete(f);
            File.Delete(invalidParent);
        }
    }
}
