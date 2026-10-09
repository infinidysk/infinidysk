namespace NzbWebDAV.Clients.Usenet.Contexts;

/// <summary>
/// Holds a shared-stream upstream's prefetch while no reader is attached, so a draining
/// entry stops claiming provider connections that attached readers need.
/// </summary>
public sealed class SharedStreamDemandGate
{
    private readonly object _lock = new();
    private TaskCompletionSource? _resumed;

    public bool IsIdle
    {
        get { lock (_lock) return _resumed is not null; }
    }

    public void SetIdle()
    {
        lock (_lock)
            _resumed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void SetDemand()
    {
        TaskCompletionSource? resumed;
        lock (_lock)
        {
            resumed = _resumed;
            _resumed = null;
        }

        resumed?.TrySetResult();
    }

    public Task WaitForDemandAsync(CancellationToken cancellationToken)
    {
        Task? resumed;
        lock (_lock)
            resumed = _resumed?.Task;
        return resumed is null ? Task.CompletedTask : resumed.WaitAsync(cancellationToken);
    }
}
