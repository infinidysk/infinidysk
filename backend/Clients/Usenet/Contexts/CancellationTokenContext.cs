using System.Collections.Concurrent;

namespace NzbWebDAV.Clients.Usenet.Contexts;

public sealed class CancellationTokenContext : IDisposable
{
    private static readonly ConcurrentDictionary<LookupKey, object?> Context = new();

    private LookupKey _lookupKey;

    private CancellationTokenContext(LookupKey lookupKey)
    {
        _lookupKey = lookupKey;
    }

#pragma warning disable CA1068 // extension method on CancellationToken: the token must remain the this (first) parameter
    public static CancellationTokenContext SetContext<T>(CancellationToken ct, T? value)
    {
        var lookupKey = new LookupKey() { CancellationToken = ct, Type = typeof(T) };
        Context[lookupKey] = value;
        return new CancellationTokenContext(lookupKey);
    }
#pragma warning restore CA1068

    public static T? GetContext<T>(CancellationToken ct)
    {
        var lookupKey = new LookupKey() { CancellationToken = ct, Type = typeof(T) };
        return Context.TryGetValue(lookupKey, out var result) && result is T context ? context : default;
    }

    public void Dispose()
    {
        Context.Remove(_lookupKey, out _);
    }

    private record struct LookupKey
    {
        public CancellationToken CancellationToken;
        public Type Type;
    }
}
