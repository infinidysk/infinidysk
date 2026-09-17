namespace NzbWebDAV.Clients.Usenet.Contexts;

internal sealed class Par2VerificationReadContext : IDisposable
{
    private static readonly AsyncLocal<MultiConnectionNntpClient?> Active = new();
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The parent scope borrows this provider from the client pool; restoration must not dispose it.")]
    private readonly MultiConnectionNntpClient? _previousProvider = Active.Value;
    private readonly MultiProviderNntpClient.ResponderAttribution? _previousAttribution =
        MultiProviderNntpClient.AttributionContext.Value;

    internal Par2VerificationReadContext(MultiConnectionNntpClient provider)
    {
        Active.Value = provider;
        MultiProviderNntpClient.AttributionContext.Value = new MultiProviderNntpClient.ResponderAttribution();
    }

    internal static MultiConnectionNntpClient? PreferredProvider => Active.Value;

    public void Dispose()
    {
        Active.Value = _previousProvider;
        MultiProviderNntpClient.AttributionContext.Value = _previousAttribution;
    }
}