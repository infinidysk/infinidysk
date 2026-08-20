using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Config;

/// <summary>
/// Validated config writes and persistence. Read-only services should take
/// <see cref="IConfigReader"/> or <see cref="IConfigChangeSource"/> instead.
/// </summary>
public interface IConfigUpdater
{
    Task LoadConfig();
    void ApplyEnvironmentOverlay(ConfigEnvironmentOverlay overlay);
    void UpdateValues(List<ConfigItem> configItems);
}
