namespace NzbWebDAV.Config;

/// <summary>
/// Typed and key-based config reads. Mutable persistence lives on
/// <see cref="IConfigUpdater"/>; change notifications on
/// <see cref="IConfigChangeSource"/>.
/// </summary>
public interface IConfigReader
{
    string? GetEffectiveConfigValue(string configName);
    string? GetPersistedConfigValue(string configName);
    bool IsEnvironmentManaged(string configName);
    string? GetEnvironmentVariableName(string configName);
}
