namespace NzbWebDAV.Exceptions;

public sealed class ConnectionOpenTimeoutException(
    string provider,
    string phase,
    TimeSpan budget,
    bool factoryStarted,
    Exception? innerException = null)
    : RetryableDownloadException(
        $"Connection opening for provider '{provider}' exceeded the {budget.TotalSeconds:0}s budget during {phase}.",
        innerException)
{
    public string Phase { get; } = phase;
    public bool FactoryStarted { get; } = factoryStarted;
}