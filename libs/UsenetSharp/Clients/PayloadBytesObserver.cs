using System.Diagnostics;

namespace UsenetSharp.Clients;

internal static class PayloadBytesObserver
{
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