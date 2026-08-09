using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

internal sealed class DeferredArticleBodyCallback
{
    private readonly object _lock = new();
    private ArticleBodyCompletionHandler? _target;
    private (ArticleBodyResult Result, string? FailureReason)? _deferred;
    private bool _invoked;
    private bool _discarded;

    public void Invoke(ArticleBodyResult result, string? failureReason = null)
    {
        ArticleBodyCompletionHandler? target;
        lock (_lock)
        {
            if (_discarded || _invoked) return;
            _invoked = true;
            target = _target;
            if (target == null)
            {
                _deferred ??= (result, failureReason);
                return;
            }
        }

        InvokeSafely(target, result, failureReason);
    }

    public void Activate(ArticleBodyCompletionHandler target)
    {
        (ArticleBodyResult Result, string? FailureReason)? deferred;
        lock (_lock)
        {
            if (_discarded) return;
            _target = target;
            deferred = _deferred;
            _deferred = null;
        }

        if (deferred.HasValue)
        {
            InvokeSafely(target, deferred.Value.Result, deferred.Value.FailureReason);
        }
    }

    public void Discard()
    {
        lock (_lock)
        {
            _discarded = true;
            _target = null;
            _deferred = null;
        }
    }

    private static void InvokeSafely(
        ArticleBodyCompletionHandler target,
        ArticleBodyResult result,
        string? failureReason)
    {
        try
        {
            target(result, failureReason);
        }
        catch
        {
            // Completion callbacks must not fault NNTP transfer tasks.
        }
    }
}
