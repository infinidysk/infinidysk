using NzbWebDAV.Clients.Usenet;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class DeferredArticleBodyCallbackTests
{
    [Fact]
    public void InvokeBeforeActivate_DeliversDeferredResult()
    {
        var callback = new DeferredArticleBodyCallback();
        var results = new List<ArticleBodyResult>();

        callback.Invoke(ArticleBodyResult.Retrieved);
        Assert.Empty(results);
        callback.Activate((result, _) => results.Add(result));

        Assert.Equal([ArticleBodyResult.Retrieved], results);
    }

    [Fact]
    public void InvokeAfterActivate_DeliversImmediately()
    {
        var callback = new DeferredArticleBodyCallback();
        var results = new List<ArticleBodyResult>();
        callback.Activate((result, _) => results.Add(result));

        callback.Invoke(ArticleBodyResult.NotFound);

        Assert.Equal([ArticleBodyResult.NotFound], results);
    }

    [Fact]
    public void InvokeMoreThanOnce_DeliversOnlyTheFirstResult()
    {
        var callback = new DeferredArticleBodyCallback();
        var results = new List<ArticleBodyResult>();
        callback.Activate((result, _) => results.Add(result));

        callback.Invoke(ArticleBodyResult.Cancelled);
        callback.Invoke(ArticleBodyResult.Retrieved);

        Assert.Equal([ArticleBodyResult.Cancelled], results);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Discard_SuppressesPendingAndFutureResults(bool invokeBeforeDiscard)
    {
        var callback = new DeferredArticleBodyCallback();
        var results = new List<ArticleBodyResult>();
        if (invokeBeforeDiscard)
            callback.Invoke(ArticleBodyResult.NotRetrieved);

        callback.Discard();
        callback.Activate((result, _) => results.Add(result));
        callback.Invoke(ArticleBodyResult.Retrieved);

        Assert.Empty(results);
    }

    [Fact]
    public void ThrowingTarget_IsContained()
    {
        var callback = new DeferredArticleBodyCallback();
        callback.Activate((_, _) => throw new InvalidOperationException("callback failure"));

        var exception = Record.Exception(() => callback.Invoke(ArticleBodyResult.Retrieved));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ConcurrentInvokeAndActivate_DeliversExactlyOnce()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var callback = new DeferredArticleBodyCallback();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivered = 0;
            var invoke = Task.Run(async () =>
            {
                await ready.Task;
                callback.Invoke(ArticleBodyResult.Retrieved);
            });
            var activate = Task.Run(async () =>
            {
                await ready.Task;
                callback.Activate((_, _) => Interlocked.Increment(ref delivered));
            });

            ready.SetResult();
            await Task.WhenAll(invoke, activate);
            callback.Invoke(ArticleBodyResult.NotRetrieved);

            Assert.Equal(1, Volatile.Read(ref delivered));
        }
    }
}
