using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace SharpCompress.Test.Mocks;

/// <summary>
/// Provides non-array-backed <see cref="Memory{T}"/> for tests that must exercise
/// code paths where <see cref="MemoryMarshal.TryGetArray{T}"/> fails.
/// </summary>
internal sealed class NativeBufferMemoryManager(int length) : MemoryManager<byte>
{
    private readonly nint _pointer = Marshal.AllocHGlobal(length);
    private readonly int _length = length;

    public override unsafe Span<byte> GetSpan() => new((void*)_pointer, _length);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if (elementIndex < 0 || elementIndex >= _length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }

        unsafe
        {
            return new MemoryHandle((void*)(_pointer + elementIndex));
        }
    }

    public override void Unpin() { }

    protected override void Dispose(bool disposing)
    {
        Marshal.FreeHGlobal(_pointer);
    }
}
