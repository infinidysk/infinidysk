using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Logging;

internal enum SynchronousObserverSource
{
    ConnectionPoolChanged,
    ConnectionLimitLearned,
    ConfigChanged,
    SharedStreamRingRetainedBytes,
    SharedStreamForceEvictions,
}

/// <summary>
/// Invokes multicast telemetry observers one subscriber at a time. A non-fatal
/// exception from one subscriber is logged and cannot skip later subscribers or
/// fault the owner. <see cref="OutOfMemoryException"/> is never contained.
/// </summary>
internal static class SynchronousObserverInvoker
{
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(1);
    private static readonly LogThrottle FailureLogThrottle = new();

    internal static void ResetFailureLogThrottleForTests()
        => FailureLogThrottle.Clear();

    public static void Invoke<TEventArgs>(
        EventHandler<TEventArgs>? subscribers,
        object sender,
        TEventArgs args,
        SynchronousObserverSource source)
        where TEventArgs : EventArgs
    {
        if (subscribers is null)
            return;

        var snapshot = subscribers.GetInvocationList().Cast<EventHandler<TEventArgs>>();
        foreach (var subscriber in snapshot)
        {
            try
            {
                subscriber(sender, args);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogFailure(source, subscriber, exception);
            }
        }
    }

    public static void Invoke<T>(
        Action<T>? subscribers,
        T argument,
        SynchronousObserverSource source)
    {
        if (subscribers is null)
            return;

        var snapshot = subscribers.GetInvocationList().Cast<Action<T>>();
        foreach (var subscriber in snapshot)
        {
            try
            {
                subscriber(argument);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogFailure(source, subscriber, exception);
            }
        }
    }

    public static void Invoke<T1, T2>(
        Action<T1, T2>? subscribers,
        T1 first,
        T2 second,
        SynchronousObserverSource source)
    {
        if (subscribers is null)
            return;

        var snapshot = subscribers.GetInvocationList().Cast<Action<T1, T2>>();
        foreach (var subscriber in snapshot)
        {
            try
            {
                subscriber(first, second);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogFailure(source, subscriber, exception);
            }
        }
    }

    private static void LogFailure(
        SynchronousObserverSource source,
        Delegate subscriber,
        Exception exception)
    {
        if (!FailureLogThrottle.ShouldLog(
                source.ToString(),
                FailureLogInterval,
                out var suppressed))
        {
            return;
        }

        var subscriberType = subscriber.Method.DeclaringType?.FullName ?? "unknown";
        var subscriberMethod = subscriber.Method.Name;

        if (exception.TryGetKnownErrorMessage(out _))
        {
            Log.Warning(
                "Synchronous observer failed. Source: {ObserverSource} " +
                "Subscriber: {SubscriberType}.{SubscriberMethod} " +
                "ExceptionType: {ExceptionType} Suppressed: {SuppressedCount} " +
                "Reason: subscriber dependency reported a known failure",
                source,
                subscriberType,
                subscriberMethod,
                exception.GetType().FullName,
                suppressed);
            Log.Debug(
                "Synchronous observer known failure stack. Source: {ObserverSource} " +
                "StackTrace: {ObserverStackTrace}",
                source,
                exception.StackTrace);
            return;
        }

        Log.Error(
            "Unexpected synchronous observer failure. Source: {ObserverSource} " +
            "Subscriber: {SubscriberType}.{SubscriberMethod} " +
            "ExceptionType: {ExceptionType} Suppressed: {SuppressedCount} " +
            "StackTrace: {ObserverStackTrace}",
            source,
            subscriberType,
            subscriberMethod,
            exception.GetType().FullName,
            suppressed,
            exception.StackTrace);
    }
}
