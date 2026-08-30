using UsenetSharp.Models;

namespace NzbWebDAV.Tests.TestUtils;

internal sealed class ArticleBodyCompletionRecorder(bool throwOnInvoke = false)
{
    private int _count;

    public bool ThrowOnInvoke { get; set; } = throwOnInvoke;
    public int Count => Volatile.Read(ref _count);
    public ArticleBodyResult? Result { get; private set; }
    public string? FailureReason { get; private set; }

    public void Invoke(ArticleBodyResult result, string? failureReason)
    {
        Interlocked.Increment(ref _count);
        Result = result;
        FailureReason = failureReason;
        if (ThrowOnInvoke)
            throw new InvalidOperationException("callback failure");
    }
}
