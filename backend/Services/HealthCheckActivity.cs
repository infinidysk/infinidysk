namespace NzbWebDAV.Services;

/// <summary>
/// Ambient heartbeat that lets nested work (STAT sweeps, inline PAR2 repair reads and phase
/// changes) extend the enclosing health check's inactivity watchdog.
/// </summary>
internal static class HealthCheckActivity
{
    private static readonly AsyncLocal<Action?> Heartbeat = new();

    public static Action? Current
    {
        get => Heartbeat.Value;
        set => Heartbeat.Value = value;
    }

    public static void Report() => Heartbeat.Value?.Invoke();
}
