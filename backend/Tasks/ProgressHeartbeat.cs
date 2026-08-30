using Serilog;

namespace NzbWebDAV.Tasks;

internal enum ProgressHeartbeatOperation
{
    PruneCompletedHistory,
    RemoveUnlinkedFiles,
    RemoveMissingPayloads,
}

internal sealed class ProgressHeartbeat : IAsyncDisposable
{
    private enum ProgressReportSource
    {
        StartPhase,
        UpdatePhase,
        ScheduledHeartbeat,
        Complete,
    }

    private readonly object _sync = new();
    private readonly Action<string> _report;
    private readonly TimeSpan _interval;
    private readonly ProgressHeartbeatOperation _operation;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _timer;
    private string? _message;
    private long _runStartedAt;
    private bool _hasStarted;
    private bool _completed;
    private bool _disposed;
    private bool _reportFailureLogged;

    internal ProgressHeartbeat(
        Action<string> report,
        TimeSpan interval,
        ProgressHeartbeatOperation operation,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        _report = report;
        _interval = interval;
        _operation = operation;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timer = _timeProvider.CreateTimer(
            static state => ((ProgressHeartbeat)state!).ReportHeartbeat(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    internal void StartPhase(string message)
    {
        lock (_sync)
        {
            if (_disposed || _completed) return;
            if (!_hasStarted)
            {
                _runStartedAt = _timeProvider.GetTimestamp();
                _hasStarted = true;
            }
            _message = message;
            ReportWithElapsed(ProgressReportSource.StartPhase);
            _timer.Change(_interval, _interval);
        }
    }

    internal void UpdatePhase(string message)
    {
        lock (_sync)
        {
            if (_disposed || _completed) return;
            _message = message;
            ReportWithElapsed(ProgressReportSource.UpdatePhase);
        }
    }

    internal void Complete(string message)
    {
        lock (_sync)
        {
            if (_disposed || _completed) return;
            _completed = true;
            _message = null;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            ReportSafely(message, ProgressReportSource.Complete);
        }
    }

    private void ReportHeartbeat()
    {
        lock (_sync)
        {
            if (_disposed || _message is null) return;
            ReportWithElapsed(ProgressReportSource.ScheduledHeartbeat);
        }
    }

    private void ReportWithElapsed(ProgressReportSource source)
    {
        if (_message is null) return;
        if (!_hasStarted)
        {
            _runStartedAt = _timeProvider.GetTimestamp();
            _hasStarted = true;
        }
        var elapsed = _timeProvider.GetElapsedTime(_runStartedAt);
        ReportSafely($"{_message}\nElapsed: {FormatElapsed(elapsed)}", source);
    }

    private void ReportSafely(string message, ProgressReportSource source)
    {
        try
        {
            _report(message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_reportFailureLogged) return;
            _reportFailureLogged = true;
            Log.Warning(
                "Progress reporting failed; maintenance continues. " +
                "Operation={Operation} Source={ReportSource} ExceptionType={ExceptionType}",
                _operation,
                source,
                exception.GetType().FullName ?? exception.GetType().Name);
        }
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
            : $"{Math.Max(1, (int)elapsed.TotalSeconds)}s";

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _message = null;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        await _timer.DisposeAsync().ConfigureAwait(false);
    }
}
