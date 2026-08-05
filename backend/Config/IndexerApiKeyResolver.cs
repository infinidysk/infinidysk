using System.Text.Json;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Config;

/// <summary>
/// Resolves an indexer API key that may be a UI mask token back to the
/// stored plaintext, so test-connection can auth without forcing re-entry.
/// </summary>
public static class IndexerApiKeyResolver
{
    public const string InstancesConfigName = ConfigKeys.IndexersInstances;

    public static string Resolve(string submittedApiKey, ConfigManager configManager)
    {
        if (!ConfigSecretMasker.IsMaskToken(submittedApiKey))
            return submittedApiKey;

        var masker = new ConfigSecretMasker(
            EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY"));
        var existingJson = JsonSerializer.Serialize(configManager.GetIndexerConfig());
        return masker.ResolveMaskedJsonSecret(InstancesConfigName, submittedApiKey, existingJson);
    }
}
