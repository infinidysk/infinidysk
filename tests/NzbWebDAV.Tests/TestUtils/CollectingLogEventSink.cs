using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.TestUtils;

internal sealed class CollectingLogEventSink : ILogEventSink
{
    private readonly List<LogEvent> _events = [];

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_events) return _events.ToArray();
        }
    }

    public void Emit(LogEvent logEvent)
    {
        lock (_events) _events.Add(logEvent);
    }
}
