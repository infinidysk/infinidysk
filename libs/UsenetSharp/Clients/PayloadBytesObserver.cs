namespace UsenetSharp.Clients;

internal static class PayloadBytesObserver
{
    public static void InvokeContained(Action<int>? observer, int bytes)
    {
        if (observer is null || bytes <= 0) return;
        try { observer(bytes); }
        catch { }
    }
}