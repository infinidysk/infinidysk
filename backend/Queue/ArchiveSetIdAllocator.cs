using System.Globalization;

namespace NzbWebDAV.Queue;

internal sealed class ArchiveSetIdAllocator
{
    private int _next;

    internal string Allocate()
    {
        _next = checked(_next + 1);
        return "set:" + _next.ToString("D10", CultureInfo.InvariantCulture);
    }
}
