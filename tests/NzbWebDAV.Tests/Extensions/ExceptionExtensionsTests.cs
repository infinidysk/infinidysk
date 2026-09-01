using System.Net.Sockets;
using System.Xml;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Tests.Extensions;

public class ExceptionExtensionsTests
{
    [Fact]
    public void TryGetKnownErrorMessage_RecognizesDirectTimeout()
    {
        var ex = new TimeoutException("Timeout reading from NNTP stream.");

        Assert.True(ex.TryGetKnownErrorMessage(out var reason));
        Assert.Equal("Timeout reading from NNTP stream.", reason);
    }

    [Fact]
    public void TryGetKnownErrorMessage_PrefersInnermostKnownMessage()
    {
        var inner = new TimeoutException("Timeout reading from NNTP stream.");
        var outer = new Exception("wrapper", inner);

        Assert.True(outer.TryGetKnownErrorMessage(out var reason));
        Assert.Equal("Timeout reading from NNTP stream.", reason);
    }

    [Fact]
    public void TryGetKnownErrorMessage_CorruptedBlobPayload_PrefersWrapperOverInnerEndOfStream()
    {
        // The inner EndOfStreamException is itself a known IOException; the wrapper's
        // more actionable restore/re-download guidance must win, not the raw EOF text.
        var inner = new EndOfStreamException("Premature end of stream");
        var ex = new CorruptedBlobPayloadException(Guid.NewGuid(), "/config/blobs/aa/bb/id", typeof(object), inner);

        Assert.True(ex.TryGetKnownErrorMessage(out var reason));
        Assert.Equal(ex.Message, reason);
        Assert.DoesNotContain("Premature end of stream", reason);
    }

    [Fact]
    public void TryGetKnownErrorMessage_RecognizesUsenetUnexpectedResponse()
    {
        var ex = new UsenetUnexpectedResponseException("<seg@example>", "400 too much time between commands");

        Assert.True(ex.TryGetKnownErrorMessage(out var reason));
        Assert.Contains("Unexpected NNTP response", reason);
        Assert.Contains("<seg@example>", reason);
    }

    [Fact]
    public void TryGetKnownErrorMessage_RecognizesSocketAndIoErrors()
    {
        var socket = new SocketException((int)SocketError.ConnectionReset);
        Assert.True(socket.TryGetKnownErrorMessage(out var socketReason));
        Assert.False(string.IsNullOrWhiteSpace(socketReason));

        var io = new IOException("Unable to read data from the transport connection.");
        Assert.True(io.TryGetKnownErrorMessage(out var ioReason));
        Assert.Equal("Unable to read data from the transport connection.", ioReason);
    }

    [Fact]
    public void TryGetKnownErrorMessage_RecognizesRemoteResponseTooLarge()
    {
        var inner = new NzbResponseTooLargeException(100);
        var ex = new RemoteResponseTooLargeException(100, null, inner);

        Assert.True(ex.TryGetKnownErrorMessage(out var reason));
        Assert.Equal(ex.Message, reason);
        Assert.DoesNotContain("NZB response", reason);
        Assert.Contains("100", reason);
    }

    [Fact]
    public void TryGetKnownErrorMessage_RecognizesRemoteResponseFormat()
    {
        var ex = new RemoteResponseFormatException(
            "Indexer returned invalid XML.",
            new XmlException("secret DO_NOT_LOG_BODY_MARKER at line 1"));

        Assert.True(ex.TryGetKnownErrorMessage(out var reason));
        Assert.Equal("Indexer returned invalid XML.", reason);
        Assert.DoesNotContain("DO_NOT_LOG_BODY_MARKER", reason);
    }

    [Fact]
    public void TryGetKnownErrorMessage_DoesNotTreatInvalidOperationAsKnownRemote()
    {
        var ex = new InvalidOperationException("unexpected bug DO_NOT_LOG_BODY_MARKER");

        Assert.False(ex.TryGetKnownErrorMessage(out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void TryGetKnownErrorMessage_RejectsUnexpectedExceptions()
    {
        var ex = new NullReferenceException("unexpected bug");

        Assert.False(ex.TryGetKnownErrorMessage(out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void TryGetKnownErrorMessage_RecognizesCorruptArticle()
    {
        var ex = new UsenetCorruptArticleException(
            "segment@example",
            "provider-a",
            new InvalidDataException("The decoded yEnc CRC32 was d58e29bc, but the trailer expected df0ce5f8."));

        Assert.True(ex.TryGetKnownErrorMessage(out var reason));
        Assert.Contains("corrupt yEnc", reason);
        Assert.True(ex.IsRetryableDownloadException());
    }

    [Fact]
    public void TryGetKnownErrorMessage_RecognizesArticleNotFound()
    {
        var ex = new UsenetArticleNotFoundException("<missing@example>");

        Assert.True(ex.TryGetKnownErrorMessage(out var reason));
        Assert.Contains("<missing@example>", reason);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(TimeoutException))]
    public void IsTransientTransportException_RecognizesBareTransportErrors(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType, "blip")!;
        Assert.True(ex.IsTransientTransportException());
    }

    [Fact]
    public void IsTransientTransportException_RecognizesSocketException()
    {
        Assert.True(new SocketException((int)SocketError.ConnectionReset).IsTransientTransportException());
    }

    [Fact]
    public void IsTransientTransportException_RecognizesNestedIoOverSocket()
    {
        var nested = new IOException("reset", new SocketException((int)SocketError.ConnectionReset));
        Assert.True(nested.IsTransientTransportException());
    }

    [Fact]
    public void IsTransientTransportException_RejectsArticleNotFound()
    {
        Assert.False(new UsenetArticleNotFoundException("<missing@example>").IsTransientTransportException());
    }

    [Fact]
    public void TryGetCausingException_FindsCauseInsideAggregateException()
    {
        var inner = new TimeoutException("Timeout reading from NNTP stream.");
        var aggregate = new AggregateException("batch failed", new Exception("noise"), inner);

        Assert.True(aggregate.TryGetCausingException<TimeoutException>(out var found));
        Assert.Same(inner, found);
    }

    [Fact]
    public void IsDatabaseCorruptionException_RecognizesSqliteCorrupt()
    {
        var ex = new SqliteException("SQLite Error 11: 'database disk image is malformed'.", 11);

        Assert.True(ex.IsDatabaseCorruptionException());
    }

    [Fact]
    public void IsDatabaseCorruptionException_RecognizesNestedCorrupt()
    {
        var inner = new SqliteException("SQLite Error 11: 'database disk image is malformed'.", 11);
        var outer = new InvalidOperationException("An error occurred while saving changes.", inner);

        Assert.True(outer.IsDatabaseCorruptionException());
    }

    [Theory]
    [InlineData(5)] // SQLITE_BUSY
    [InlineData(6)] // SQLITE_LOCKED
    [InlineData(8)] // SQLITE_READONLY
    [InlineData(13)] // SQLITE_FULL
    [InlineData(26)] // SQLITE_NOTADB
    public void IsDatabaseCorruptionException_RejectsTransientAndOtherSqliteErrors(int errorCode)
    {
        var ex = new SqliteException("some other sqlite error", errorCode);

        Assert.False(ex.IsDatabaseCorruptionException());
    }

    [Fact]
    public void IsDuplicateSchemaObjectException_RecognizesExistingSqliteObject()
    {
        var ex = new SqliteException(
            "SQLite Error 1: 'index IX_HealthCheckResults_RepairStatus_CreatedAt already exists'.",
            1);

        Assert.True(ex.IsDuplicateSchemaObjectException());
    }

    [Fact]
    public void IsDuplicateSchemaObjectException_RecognizesWrappedExistingSqliteObject()
    {
        var ex = new InvalidOperationException(
            "Migration failed.",
            new SqliteException("SQLite Error 1: 'table Foo already exists'.", 1));

        Assert.True(ex.IsDuplicateSchemaObjectException());
    }

    [Theory]
    [InlineData(1, "SQLite Error 1: 'near \"BROKEN\": syntax error'.")]
    [InlineData(5, "SQLite Error 5: 'database is locked'.")]
    public void IsDuplicateSchemaObjectException_RejectsOtherSqliteErrors(int errorCode, string message)
    {
        var ex = new SqliteException(message, errorCode);

        Assert.False(ex.IsDuplicateSchemaObjectException());
    }

    [Fact]
    public void TryGetKnownErrorMessage_Corruption_ReturnsRecoveryGuidance()
    {
        var ex = new SqliteException("SQLite Error 11: 'database disk image is malformed'.", 11);

        Assert.True(ex.TryGetKnownErrorMessage(out var reason));
        Assert.Contains("corrupt", reason);
        Assert.Contains("Backup & Restore", reason);
    }

    [Fact]
    public void TryGetKnownErrorMessage_RecognizesSqliteBusy()
    {
        var ex = new SqliteException("SQLite Error 5: 'database is locked'.", 5);

        Assert.True(ex.TryGetKnownErrorMessage(out var reason));
        Assert.Contains("locked", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void IsTransientDatabaseException_RecognizesBusyAndLocked(int errorCode)
    {
        var ex = new SqliteException("sqlite contention", errorCode);

        Assert.True(ex.IsTransientDatabaseException());
        Assert.False(ex.IsKnownSqliteDiskException());
    }

    [Theory]
    [InlineData(5, 261)] // SQLITE_BUSY_RECOVERY
    [InlineData(5, 517)] // SQLITE_BUSY_SNAPSHOT
    [InlineData(6, 262)] // SQLITE_LOCKED_SHAREDCACHE
    [InlineData(6, 518)] // SQLITE_LOCKED_VTAB
    public void IsSqliteBusyOrLockedException_UsesPrimaryCode(
        int primaryCode,
        int extendedCode)
    {
        var exception = new SqliteException("sqlite contention", primaryCode, extendedCode);

        Assert.Equal(primaryCode, exception.SqliteErrorCode);
        Assert.Equal(extendedCode, exception.SqliteExtendedErrorCode);
        Assert.True(exception.IsSqliteBusyOrLockedException());
        Assert.True(exception.IsTransientDatabaseException());
    }

    [Fact]
    public void IsSqliteBusyOrLockedException_RecognizesWrappedBusy()
    {
        var inner = new SqliteException("sqlite contention", 5, 261);
        var outer = new DbUpdateException("wrapper", inner);

        Assert.True(outer.IsSqliteBusyOrLockedException());
        Assert.True(outer.IsTransientDatabaseException());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(19)]
    [InlineData(26)]
    public void IsSqliteBusyOrLockedException_RejectsNonContentionCodes(int primaryCode)
    {
        var exception = new SqliteException("other sqlite error", primaryCode);

        Assert.False(exception.IsSqliteBusyOrLockedException());
    }

    [Theory]
    [InlineData(8, "SQLite Error 8: 'attempt to write a readonly database'.")]
    [InlineData(13, "SQLite Error 13: 'database or disk is full'.")]
    public void SqliteReadonlyAndFull_AreKnownButNotTransient(int errorCode, string message)
    {
        var ex = new SqliteException(message, errorCode);

        Assert.False(ex.IsTransientDatabaseException());
        Assert.True(ex.IsKnownSqliteDiskException());
        Assert.True(ex.TryGetKnownErrorMessage(out var reason));
        Assert.Equal(message, reason);
    }

    [Fact]
    public void IsTransientTransportException_RejectsAlreadyRetryable()
    {
        Assert.False(new RetryableDownloadException("already classified", new IOException("inner"))
            .IsTransientTransportException());
    }
}
