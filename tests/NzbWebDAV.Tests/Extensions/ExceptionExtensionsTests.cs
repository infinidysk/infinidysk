using System.Net.Sockets;
using Microsoft.Data.Sqlite;
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
    public void TryGetKnownErrorMessage_Corruption_ReturnsRecoveryGuidance()
    {
        var ex = new SqliteException("SQLite Error 11: 'database disk image is malformed'.", 11);

        Assert.True(ex.TryGetKnownErrorMessage(out var reason));
        Assert.Contains("corrupt", reason);
        Assert.Contains("Backup & Restore", reason);
    }

    [Fact]
    public void TryGetKnownErrorMessage_OtherSqliteErrors_StayUnknown()
    {
        // Busy/locked and friends stay unclassified: they are transient and
        // handled by their own retry paths, not by corruption guidance.
        var ex = new SqliteException("SQLite Error 5: 'database is locked'.", 5);

        Assert.False(ex.TryGetKnownErrorMessage(out _));
    }

    [Fact]
    public void IsTransientTransportException_RejectsAlreadyRetryable()
    {
        Assert.False(new RetryableDownloadException("already classified", new IOException("inner"))
            .IsTransientTransportException());
    }
}
