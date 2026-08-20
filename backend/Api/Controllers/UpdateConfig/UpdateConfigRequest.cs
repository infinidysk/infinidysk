using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.UpdateConfig;

public class UpdateConfigRequest
{
    public const int MaxConfigNameLength = 256;
    public const int MaxConfigValueLength = 1_000_000;

    public List<ConfigItem> ConfigItems { get; init; }

    public UpdateConfigRequest(HttpContext context)
    {
        var errors = new ValidationErrors();
        if (!context.Request.HasFormContentType)
        {
            errors.Add("config", "Config updates must be submitted as form fields.");
            errors.ThrowIfAny();
        }

        var items = new List<ConfigItem>();
        foreach (var pair in context.Request.Form)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                errors.Add("config", "Config names must be non-empty strings.");
                continue;
            }

            if (pair.Key.Length > MaxConfigNameLength)
            {
                errors.Add(pair.Key, "Config name exceeds the maximum length.");
                continue;
            }

            if (pair.Value.Count > 1)
            {
                errors.Add(pair.Key, "Config values must be a single string.");
                continue;
            }

            var value = pair.Value.FirstOrDefault();
            if (value is null)
            {
                errors.Add(pair.Key, "Config values must be strings.");
                continue;
            }

            if (value.Length > MaxConfigValueLength)
            {
                errors.Add(pair.Key, "Config value exceeds the maximum length.");
                continue;
            }

            items.Add(new ConfigItem
            {
                ConfigName = pair.Key,
                ConfigValue = value
            });
        }

        errors.ThrowIfAny();
        ConfigItems = items;
    }
}
