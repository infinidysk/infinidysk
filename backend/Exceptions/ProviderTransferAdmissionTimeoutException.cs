namespace NzbWebDAV.Exceptions;

internal sealed class ProviderTransferAdmissionTimeoutException(
    string providerName,
    TimeSpan timeout,
    string phase = "ProviderAdmission")
    : RetryableDownloadException(
        $"Provider '{providerName}' waited {timeout.TotalSeconds:0.#}s " +
        $"for an NNTP connection during {phase}.")
{
    public string ProviderName { get; } = providerName;
    public TimeSpan Timeout { get; } = timeout;
    public string Phase { get; } = phase;
}
