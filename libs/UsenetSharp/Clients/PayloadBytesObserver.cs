using System.Diagnostics;

namespace UsenetSharp.Clients;

internal static class PayloadBytesObserver
{
    /// <summary>
    /// Body readers report payload a line at a time (~130 bytes for yEnc). Handing each line
    /// to the observer makes it the hottest call in the process, and observers that update
    /// shared per-provider state contend on it across every connection. Callers accumulate
    /// and report at most once per this many bytes, plus once on exit.
    /// </summary>
    internal const int CoalesceThreshold = 64 * 1024;

    public static void Accumulate(Action<int>? observer, ref int pending, int bytes)
    {
        if (observer is null || bytes <= 0) return;
        pending += bytes;
        if (pending >= CoalesceThreshold) Flush(observer, ref pending);
    }

    public static void Flush(Action<int>? observer, ref int pending)
    {
        var bytes = pending;
        pending = 0;
        InvokeContained(observer, bytes);
    }

    public static void InvokeContained(Action<int>? observer, int bytes)
    {
        if (observer is null || bytes <= 0) return;
        try { observer(bytes); }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            Debug.WriteLine($"UsenetSharp payload observer failed: {exception}");
        }
    }
}