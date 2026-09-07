namespace NzbWebDAV.Utils;

internal sealed class DisposableOwner<T>(Func<T>? create = null) : IDisposable where T : class, IDisposable
{
    private T? _value = create?.Invoke();

    public T Value => _value ?? throw new InvalidOperationException("The scope does not own a resource.");

    public void TakeOwnership(Func<T> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        if (_value is not null)
            throw new InvalidOperationException("The scope already owns a resource.");
        _value = create();
    }

    public T? ReleaseOwnership()
    {
        var resource = _value;
        _value = null;
        return resource;
    }

    public void Dispose() => ReleaseOwnership()?.Dispose();
}