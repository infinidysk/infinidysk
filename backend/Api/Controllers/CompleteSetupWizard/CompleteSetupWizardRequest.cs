using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.CompleteSetupWizard;

public sealed class CompleteSetupWizardRequest
{
    public string Strategy { get; }
    public string[] IngestionMethods { get; }
    public List<ConfigItem> ConfigItems { get; }

    public CompleteSetupWizardRequest(HttpContext context)
    {
        var errors = new ValidationErrors();
        if (!context.Request.HasFormContentType)
        {
            errors.Add("config", "Setup completion must be submitted as form fields.");
            errors.ThrowIfAny();
        }

        Strategy = context.Request.Form["strategy"].FirstOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(Strategy))
            errors.Add("strategy", "Library strategy is required.");

        IngestionMethods = ParseStringArray(
            context.Request.Form["ingestionMethods"].FirstOrDefault(),
            "ingestionMethods",
            errors);
        ConfigItems = ParseConfigItems(
            context.Request.Form["config"].FirstOrDefault(),
            errors);
        errors.ThrowIfAny();
    }

    private static string[] ParseStringArray(
        string? value,
        string field,
        ValidationErrors errors)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<string[]>(value ?? "");
            if (parsed is not null) return parsed;
        }
        catch (JsonException)
        {
        }

        errors.Add(field, "Ingestion methods must be a JSON array of strings.");
        return [];
    }

    private static List<ConfigItem> ParseConfigItems(string? value, ValidationErrors errors)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(value ?? "");
            if (parsed is not null)
            {
                return parsed.Select(pair => new ConfigItem
                {
                    ConfigName = pair.Key,
                    ConfigValue = pair.Value,
                }).ToList();
            }
        }
        catch (JsonException)
        {
        }

        errors.Add("config", "Setup configuration must be a JSON object of string values.");
        return [];
    }
}
