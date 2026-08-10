using Serilog.Events;

namespace NzbWebDAV.Logging;

internal static class NWebDavLogFilter
{
    private const string PropFindHandlerSource = "NWebDav.Server.Handlers.PropFindHandler";
    private const string PropertyErrorPrefix = "Property ";

    internal static bool IsCancelledPropFindPropertyError(LogEvent logEvent)
    {
        return logEvent.Exception is OperationCanceledException
               && logEvent.Properties.TryGetValue("SourceContext", out var sourceContext)
               && sourceContext is ScalarValue { Value: string source }
               && string.Equals(source, PropFindHandlerSource, StringComparison.Ordinal)
               && logEvent.MessageTemplate.Text.StartsWith(PropertyErrorPrefix, StringComparison.Ordinal);
    }

    // NWebDav.Server 0.2.0-beta.2 logs every unsupported PROPFIND property as a
    // Warning ("Property {Name} is not supported"). Prefix matching survives minor
    // template wording changes across NWebDav releases.
    internal static bool IsUnsupportedPropFindPropertyWarning(LogEvent logEvent)
    {
        return logEvent.Level == LogEventLevel.Warning
               && logEvent.Properties.TryGetValue("SourceContext", out var sourceContext)
               && sourceContext is ScalarValue { Value: string source }
               && string.Equals(source, PropFindHandlerSource, StringComparison.Ordinal)
               && logEvent.MessageTemplate.Text.StartsWith(PropertyErrorPrefix, StringComparison.Ordinal)
               && logEvent.MessageTemplate.Text.Contains("is not supported", StringComparison.Ordinal);
    }
}
