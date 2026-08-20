using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace NzbWebDAV.Tests.TestUtils;

internal static class JsonContractValidator
{
    private static readonly ConcurrentDictionary<string, JsonSchema> Schemas = new(StringComparer.Ordinal);

    public static void AssertMatchesSchema(JsonElement instance, string relativeSchemaPath)
    {
        var schemaPath = ResolveSchemaPath(relativeSchemaPath);
        var schema = Schemas.GetOrAdd(schemaPath, path => JsonSchema.FromFile(path));
        var result = schema.Evaluate(instance, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List
        });
        if (result.IsValid)
            return;

        var details = DescribeFailures(result, instance);
        Assert.Fail(
            $"JSON did not match contract {relativeSchemaPath}.{Environment.NewLine}{details}");
    }

    internal static string ResolveSchemaPath(string relativeSchemaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeSchemaPath);
        if (Path.IsPathRooted(relativeSchemaPath)
            || relativeSchemaPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Contains("..", StringComparer.Ordinal))
        {
            throw new ArgumentException("Contract schema path must be a relative path without '..'.", nameof(relativeSchemaPath));
        }
        var fromOutput = Path.Join(AppContext.BaseDirectory, "contracts", relativeSchemaPath);
        if (File.Exists(fromOutput))
            return fromOutput;

        var fromRepo = Path.Join(RepoPaths.FindRepoRoot(), "contracts", relativeSchemaPath);
        if (File.Exists(fromRepo))
            return fromRepo;

        throw new FileNotFoundException($"Contract schema not found: {relativeSchemaPath}");
    }

    private static string DescribeFailures(EvaluationResults result, JsonElement root)
    {
        var lines = new StringBuilder();
        foreach (var detail in Flatten(result).Where(item => !item.IsValid))
        {
            var path = detail.InstanceLocation.ToString();
            if (string.IsNullOrEmpty(path))
                path = "/";
            var actualKind = DescribeKind(root, path);
            var errors = detail.Errors is { Count: > 0 }
                ? string.Join("; ", detail.Errors.Select(error => $"{error.Key}: {error.Value}"))
                : "constraint failed";
            lines.Append(path)
                .Append(": ")
                .Append(errors)
                .Append(" (actual ")
                .Append(actualKind)
                .Append(')')
                .AppendLine();
        }

        return lines.Length == 0 ? "unknown schema failure" : lines.ToString().TrimEnd();
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults result)
    {
        yield return result;
        foreach (var child in result.Details ?? [])
        {
            foreach (var nested in Flatten(child))
                yield return nested;
        }
    }

    private static string DescribeKind(JsonElement root, string jsonPointer)
    {
        if (string.IsNullOrEmpty(jsonPointer) || jsonPointer == "/")
            return KindName(root);

        var current = root;
        foreach (var segment in jsonPointer.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var decoded = Uri.UnescapeDataString(segment.Replace("~1", "/").Replace("~0", "~"));
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(decoded, out var index))
            {
                if (index < 0 || index >= current.GetArrayLength())
                    return "missing";
                current = current[index];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(decoded, out var next))
                return "missing";
            current = next;
        }

        return KindName(current);
    }

    private static string KindName(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => element.TryGetInt64(out _) ? "integer" : "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "unknown"
    };
}
