using System;
using System.Buffers;

namespace SharpCompress.Compressors.Rar.VM;

internal class BitInput : IDisposable
{
    /// <summary> the max size of the input</summary>
    internal const int MAX_SIZE = 0x8000;

    public int inAddr;
    public int inBit;

    public int InAddr
    {
        get => inAddr;
        set => inAddr = value;
    }
    public int InBit
    {
        get => inBit;
        set => inBit = value;
    }
    private readonly byte[] _privateBuffer;
    private bool _disposed;

    /// <summary>  </summary>
    internal BitInput()
    {
        _privateBuffer = ArrayPool<byte>.Shared.Rent(MAX_SIZE);
        InBuf = _privateBuffer;
    }

    internal byte[] InBuf { get; }

    internal void InitBitInput()
    {
        inAddr = 0;
        inBit = 0;
    }

    internal void faddbits(uint bits) => AddBits((int)bits);

    /// <summary>
    /// also named faddbits
    /// </summary>
    /// <param name="bits"></param>
    internal void AddBits(int bits)
    {
        bits += inBit;
        inAddr += (bits >> 3);
        inBit = bits & 7;
    }

    internal uint fgetbits() => (uint)GetBits();

    internal uint getbits() => (uint)GetBits();

    /// <summary>
    /// (also named fgetbits)
    /// </summary>
    /// <returns>
    /// the bits (unsigned short)
    /// </returns>
    internal int GetBits() =>
        //      int BitField=0;
        //      BitField|=(int)(inBuf[inAddr] << 16)&0xFF0000;
        //      BitField|=(int)(inBuf[inAddr+1] << 8)&0xff00;
        //      BitField|=(int)(inBuf[inAddr+2])&0xFF;
        //      BitField >>>= (8-inBit);
        //      return (BitField & 0xffff);
        (
            (
                (
                    ((InBuf[inAddr] & 0xff) << 16)
                    + ((InBuf[inAddr + 1] & 0xff) << 8)
                    + ((InBuf[inAddr + 2] & 0xff))
                ) >>> (8 - inBit)
            ) & 0xffff
        );

    /// <summary> Indicates an Overfow</summary>
    /// <param name="IncPtr">how many bytes to inc
    /// </param>
    /// <returns> true if an Oververflow would occur
    /// </returns>
    internal bool Overflow(int IncPtr) => (inAddr + IncPtr >= MAX_SIZE);

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        ArrayPool<byte>.Shared.Return(_privateBuffer);
        _disposed = true;
    }
}
