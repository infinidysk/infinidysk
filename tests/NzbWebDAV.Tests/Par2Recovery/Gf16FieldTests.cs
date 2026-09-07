using NzbWebDAV.Par2Recovery.ReedSolomon;

namespace NzbWebDAV.Tests.Par2Recovery;

public sealed class Gf16FieldTests
{
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 16)]
    [InlineData(3, 128)]
    [InlineData(4, 256)]
    [InlineData(5, 2048)]
    [InlineData(6, 8192)]
    [InlineData(7, 16384)]
    [InlineData(8, 4107)]
    [InlineData(9, 32856)]
    [InlineData(10, 17132)]
    public void RecoveryCoefficient_UsesPar2InputConstants(int sliceIndex, int expected)
    {
        var field = new Gf16Field();

        Assert.Equal((ushort)expected, field.RecoveryCoefficient(1, sliceIndex));
    }

    [Fact]
    public void RecoveryCoefficient_AllInputConstantsHaveFullOrder()
    {
        var field = new Gf16Field();
        var constants = new HashSet<ushort>();
        for (var sliceIndex = 0; sliceIndex < Gf16Field.MaxInputSlices; sliceIndex++)
        {
            var constant = field.RecoveryCoefficient(1, sliceIndex);
            Assert.True(constants.Add(constant));
            foreach (var factor in new[] { 3, 5, 17, 257 })
                Assert.NotEqual((ushort)1, field.Pow(constant, 65535 / factor));
            Assert.Equal((ushort)1, field.RecoveryCoefficient(0, sliceIndex));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(32768)]
    public void RecoveryCoefficient_RejectsInvalidSliceIndex(int sliceIndex)
    {
        var field = new Gf16Field();

        Assert.Throws<ArgumentOutOfRangeException>(() => field.RecoveryCoefficient(1, sliceIndex));
    }

    [Fact]
    public void RecoveryCoefficient_LargeExponentDoesNotOverflow()
    {
        var field = new Gf16Field();

        Assert.Equal(field.RecoveryCoefficient(uint.MaxValue % 65535, 32767),
            field.RecoveryCoefficient(uint.MaxValue, 32767));
        Assert.Equal(field.Pow(4107, int.MaxValue % 65535), field.Pow(4107, int.MaxValue));
    }
}