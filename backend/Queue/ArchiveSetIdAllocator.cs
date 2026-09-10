using System.Globalization;

namespace NzbWebDAV.Queue;

internal sealed class ArchiveSetIdAllocator
{
    private readonly string _prefix;
    private int _next;

    internal ArchiveSetIdAllocator(string prefix = "set")
    {
        _prefix = prefix;
    }

    internal string Allocate()
    {
        _next = checked(_next + 1);
        return _prefix + ":" + _next.ToString("D10", CultureInfo.InvariantCulture);
    }
}
