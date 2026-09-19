namespace NzbWebDAV.Exceptions;

internal sealed class ProviderTransferAdmissionTimeoutException(
    string providerName,
    TimeSpan timeout)
    : Exception($"Provider {providerName} had no transfer capacity for {timeout.TotalSeconds:0.#} seconds.")
{
    public string ProviderName { get; } = providerName;
    public TimeSpan Timeout { get; } = timeout;
}
