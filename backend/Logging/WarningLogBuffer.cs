namespace NzbWebDAV.Logging;

/// <summary>
/// Holds a second, Warning-and-above ring buffer so a flood of Debug/Information
/// events cannot evict the warnings and errors support packs are collected for.
/// The main <see cref="LogBufferSink"/> keeps only the last LOG_BUFFER_SIZE entries
/// of every level, which a chatty background service can consume within hours.
/// Exists as a distinct type purely so DI can tell the two buffers apart.
/// </summary>
public sealed class WarningLogBuffer(LogBufferSink sink)
{
    public LogBufferSink Sink { get; } = sink;
}
