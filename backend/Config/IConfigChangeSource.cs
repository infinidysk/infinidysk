namespace NzbWebDAV.Config;

/// <summary>
/// Config change notifications. This is post-commit synchronous fan-out: values are
/// already committed before handlers run. Subscriber failures are isolated and logged
/// and do not skip later subscribers. Dispatch snapshots the invocation list, so a
/// handler removed during publication may still run once for that in-flight event.
/// Subscribers must dispose the returned subscription so hosted services do not leak
/// handlers after shutdown.
/// </summary>
public interface IConfigChangeSource
{
    event EventHandler<ConfigManager.ConfigEventArgs>? OnConfigChanged;

    IDisposable Subscribe(EventHandler<ConfigManager.ConfigEventArgs> handler);
}
