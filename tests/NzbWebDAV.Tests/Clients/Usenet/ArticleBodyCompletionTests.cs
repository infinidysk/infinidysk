using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

[Collection(nameof(GlobalLoggerCollection))]
public sealed class ArticleBodyCompletionTests
{
    [Theory]
    [InlineData(ArticleBodyResult.Retrieved)]
    [InlineData(ArticleBodyResult.Cancelled)]
    [InlineData(ArticleBodyResult.NotFound)]
    [InlineData(ArticleBodyResult.NotRetrieved)]
    public void InvokeContained_NullCallback_DoesNothing(ArticleBodyResult result)
    {
        var exception = Record.Exception(() =>
            ArticleBodyCompletion.InvokeContained(null, result, "SocketException"));

        Assert.Null(exception);
    }

    [Fact]
    public void InvokeContained_ThrowingCallback_DoesNotEscape()
    {
        var count = 0;
        ArticleBodyCompletionHandler callback = (_, _) =>
        {
            count++;
            throw new InvalidOperationException("callback failure");
        };

        var exception = Record.Exception(() =>
            ArticleBodyCompletion.InvokeContained(callback, ArticleBodyResult.Retrieved));

        Assert.Null(exception);
        Assert.Equal(1, count);
    }

    [Fact]
    public void InvokeContained_PreservesResultAndFailureReason()
    {
        ArticleBodyResult? observedResult = null;
        string? observedReason = null;
        ArticleBodyCompletion.InvokeContained(
            (result, reason) =>
            {
                observedResult = result;
                observedReason = reason;
            },
            ArticleBodyResult.NotRetrieved,
            "SocketException");

        Assert.Equal(ArticleBodyResult.NotRetrieved, observedResult);
        Assert.Equal("SocketException", observedReason);
    }

    [Fact]
    public void InvokeContained_ThrowingCallback_LogsOneWarning()
    {
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var callbackException = new InvalidOperationException("callback failure");
        try
        {
            ArticleBodyCompletion.InvokeContained(
                (_, _) => throw callbackException,
                ArticleBodyResult.Retrieved);
        }
        finally
        {
            Log.Logger = previous;
        }

        var warning = Assert.Single(sink.Events, e => e.Level == LogEventLevel.Warning);
        Assert.Equal("NNTP completion callback failed for {Result}", warning.MessageTemplate.Text);
        Assert.Same(callbackException, warning.Exception);
        Assert.True(warning.Properties.TryGetValue("Result", out var resultValue));
        var scalar = Assert.IsType<ScalarValue>(resultValue);
        Assert.Equal(ArticleBodyResult.Retrieved.ToString(), scalar.Value?.ToString());
    }

    [Fact]
    public void InvokeContained_OutOfMemoryException_IsNotContained()
    {
        Assert.Throws<OutOfMemoryException>(() =>
            ArticleBodyCompletion.InvokeContained(
                (_, _) => throw new OutOfMemoryException("fatal"),
                ArticleBodyResult.Retrieved));
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events) return _events.ToArray();
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events) _events.Add(logEvent);
        }
    }
}
