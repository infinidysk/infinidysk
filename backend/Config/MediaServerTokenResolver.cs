using System.Text.Json;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Config;

/// <summary>
/// Resolves a media-server token that may be a UI mask token back to the
/// stored plaintext, so connection tests can authenticate without exposing
/// or forcing re-entry of saved credentials.
/// </summary>
public static class MediaServerTokenResolver
{
    public static string Resolve(string submittedToken, ConfigManager configManager)
    {
        if (!ConfigSecretMasker.IsMaskToken(submittedToken))
            return submittedToken;

        var masker = new ConfigSecretMasker(
            EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY"));
        var existingJson = JsonSerializer.Serialize(configManager.GetMediaServerConfig());
        return masker.ResolveMaskedJsonSecret(
            ConfigKeys.MediaServersInstances,
            submittedToken,
            existingJson);
    }
}
