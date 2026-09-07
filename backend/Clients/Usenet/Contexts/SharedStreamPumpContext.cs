namespace NzbWebDAV.Clients.Usenet.Contexts;

/// <summary>
/// Marks the async flow of a shared-stream pump. The pump starts with execution-context
/// flow suppressed so it never inherits one reader's request state, which also drops the
/// read-session id; this marker keeps its fetches attributed to client playback.
/// Attribution only — does not change admission priority at the provider gate.
/// </summary>
public sealed class SharedStreamPumpContext : IDisposable
{
    private static readonly AsyncLocal<bool> Active = new();
    private readonly bool _previous;

    private SharedStreamPumpContext()
    {
        _previous = Active.Value;
        Active.Value = true;
    }

    public static bool IsActive => Active.Value;

    public static IDisposable Begin() => new SharedStreamPumpContext();

    public void Dispose() => Active.Value = _previous;
}
