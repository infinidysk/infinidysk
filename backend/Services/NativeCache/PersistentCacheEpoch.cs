namespace NzbWebDAV.Services.NativeCache;

/// <summary>A durable fence changed before publishing any replacement repair bytes.</summary>
internal sealed class PersistentCacheEpoch
{
    private readonly string _path;
    private readonly Lock _sync = new();
    private string _value;
    private bool _active;

    public PersistentCacheEpoch(string path)
    {
        _path = path;
        _value = File.Exists(path) && new FileInfo(path).Length == 32 ? File.ReadAllText(path) : "";
        _active = _value.Length == 32 && _value.All(char.IsAsciiHexDigit);
        if (!_active) _value = Guid.NewGuid().ToString("N");
    }

    public string Value
    {
        get
        {
            lock (_sync)
            {
                if (!_active)
                {
                    Persist(_value);
                    _active = true;
                }
                return _value;
            }
        }
    }

    public void Rotate()
    {
        lock (_sync)
        {
            var value = Guid.NewGuid().ToString("N");
            // Existing installations that have never used native caching do no extra
            // filesystem writes. Once activated, retained native data requires fencing
            // repair mutations even while the selected cache mode is Segment or Off.
            if (_active) Persist(value);
            Volatile.Write(ref _value, value);
        }
    }

    private void Persist(string value)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(System.Text.Encoding.ASCII.GetBytes(value));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            NativeFileSystem.FlushDirectory(Path.GetDirectoryName(_path)!);
        }
        finally { File.Delete(temporary); }
    }
}
