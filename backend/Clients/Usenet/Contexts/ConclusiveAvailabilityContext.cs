namespace NzbWebDAV.Clients.Usenet.Contexts;

/// <summary>
/// While active, provider walks refuse to return a definitive miss when the same walk
/// also contains an unresolved provider failure; the failure is rethrown instead.
/// </summary>
internal sealed class ConclusiveAvailabilityContext : IDisposable
{
    private static readonly AsyncLocal<bool> Active = new();
    private readonly bool _previous = Active.Value;

    private ConclusiveAvailabilityContext() => Active.Value = true;

    public static bool IsActive => Active.Value;

    public static IDisposable Begin() => new ConclusiveAvailabilityContext();

    public void Dispose() => Active.Value = _previous;
}