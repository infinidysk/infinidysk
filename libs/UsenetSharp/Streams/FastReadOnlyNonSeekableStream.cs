namespace UsenetSharp.Streams;


public abstract class FastReadOnlyNonSeekableStream : FastReadOnlyStream
{
    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException("This stream does not support seeking.");

    public override long Position
    {
        get => throw new NotSupportedException("This stream does not support seeking.");
        set => throw new NotSupportedException("This stream does not support seeking.");
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException("This stream does not support seeking.");
    }
}
