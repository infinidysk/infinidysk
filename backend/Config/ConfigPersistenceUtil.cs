using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Config;

/// <summary>
/// Persists a single config key/value pair to SQLite and refreshes the running
/// <see cref="ConfigManager"/> cache. Used by call sites that update one setting
/// directly (e.g. SAB pause/resume/speedlimit) rather than through the bulk
/// settings-page save flow in UpdateConfigController.
/// </summary>
public static class ConfigPersistenceUtil
{
    public static async Task SetValueAsync(
        DavDatabaseClient dbClient,
        ConfigManager configManager,
        string configName,
        string configValue,
        CancellationToken ct = default)
    {
        var item = await dbClient.Ctx.ConfigItems
            .FirstOrDefaultAsync(c => c.ConfigName == configName, ct)
            .ConfigureAwait(false);
        if (item == null)
        {
            item = new ConfigItem { ConfigName = configName, ConfigValue = configValue };
            dbClient.Ctx.ConfigItems.Add(item);
        }
        else
        {
            item.ConfigValue = configValue;
        }

        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        configManager.UpdateValues([item]);
    }
}
