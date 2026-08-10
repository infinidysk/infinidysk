using Microsoft.Data.Sqlite;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class BackgroundServiceErrorHandlerTests
{
    private static readonly TimeSpan NormalDelay = TimeSpan.FromSeconds(10);

    [Fact]
    public void LogAndGetRetryDelay_Corruption_UsesLongBackoff()
    {
        var ex = new SqliteException("SQLite Error 11: 'database disk image is malformed'.", 11);

        var delay = BackgroundServiceErrorHandler.LogAndGetRetryDelay(ex, "test loop failed.", NormalDelay);

        Assert.Equal(BackgroundServiceErrorHandler.CorruptionDelay, delay);
    }

    [Fact]
    public void LogAndGetRetryDelay_NestedCorruption_UsesLongBackoff()
    {
        var inner = new SqliteException("SQLite Error 11: 'database disk image is malformed'.", 11);
        var ex = new InvalidOperationException("wrapper", inner);

        var delay = BackgroundServiceErrorHandler.LogAndGetRetryDelay(ex, "test loop failed.", NormalDelay);

        Assert.Equal(BackgroundServiceErrorHandler.CorruptionDelay, delay);
    }

    [Fact]
    public void LogAndGetRetryDelay_TransientSqliteError_KeepsNormalDelay()
    {
        var ex = new SqliteException("SQLite Error 5: 'database is locked'.", 5);

        var delay = BackgroundServiceErrorHandler.LogAndGetRetryDelay(ex, "test loop failed.", NormalDelay);

        Assert.Equal(NormalDelay, delay);
    }

    [Fact]
    public void LogAndGetRetryDelay_UnexpectedException_KeepsNormalDelay()
    {
        var delay = BackgroundServiceErrorHandler.LogAndGetRetryDelay(
            new NullReferenceException("unexpected bug"),
            "test loop failed.",
            NormalDelay);

        Assert.Equal(NormalDelay, delay);
    }
}
