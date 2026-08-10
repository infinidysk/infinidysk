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
        }
        finally
        {
            File.Delete(invalidParent);
        }
    }
}
