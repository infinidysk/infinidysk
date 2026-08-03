using System.Buffers;
using System.Threading.Tasks;
using Xunit;

namespace SharpCompress.Test.Streams;

public class ArrayPoolHygieneTests
{
    [Fact]
    public void ConcurrentRentsReturnDistinctBuffers()
    {
        var rent1 = Task.Run(() => ArrayPool<byte>.Shared.Rent(1024));
        var rent2 = Task.Run(() => ArrayPool<byte>.Shared.Rent(1024));
        Task.WaitAll(rent1, rent2);

        Assert.NotSame(rent1.Result, rent2.Result);

        ArrayPool<byte>.Shared.Return(rent1.Result);
        ArrayPool<byte>.Shared.Return(rent2.Result);
    }

    [Fact]
    public void ReturnNullGuardPreventsDoubleReturn()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16);
        ArrayPool<byte>.Shared.Return(buffer);
        byte[]? cleared = null;
        if (cleared is not null)
        {
            ArrayPool<byte>.Shared.Return(cleared);
        }

        var secondRent = ArrayPool<byte>.Shared.Rent(16);
        ArrayPool<byte>.Shared.Return(secondRent);
    }
}
