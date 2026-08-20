namespace NzbWebDAV.Config;

/// <summary>
/// Config change notifications. Subscribers must dispose the returned
/// subscription so hosted services do not leak handlers after shutdown.
/// </summary>
public interface IConfigChangeSource
{
    event EventHandler<ConfigManager.ConfigEventArgs>? OnConfigChanged;

    IDisposable Subscribe(EventHandler<ConfigManager.ConfigEventArgs> handler);
}
