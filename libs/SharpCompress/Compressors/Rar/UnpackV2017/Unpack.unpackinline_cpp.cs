using System;
using static SharpCompress.Compressors.Rar.UnpackV2017.PackDef;

namespace SharpCompress.Compressors.Rar.UnpackV2017;

internal partial class Unpack
{
    private void InsertOldDist(uint Distance)
    {
        OldDist[3] = OldDist[2];
        OldDist[2] = OldDist[1];
        OldDist[1] = OldDist[0];
        OldDist[0] = Distance;
    }

    //#ifdef _MSC_VER
    //#define FAST_MEMCPY
    //#endif

    private void CopyString(uint Length, uint Distance)
    {
        var SrcPtr = UnpPtr - Distance;

        if (SrcPtr < MaxWinSize - MAX_LZ_MATCH && UnpPtr < MaxWinSize - MAX_LZ_MATCH)
        {
            // If we are not close to end of window, we do not need to waste time
            // to "& MaxWinMask" pointer protection.
            var Window = this.Window;
            var src = (int)SrcPtr;
            var dest = (int)UnpPtr;
            var len = (int)Length;
            UnpPtr += Length;

            if (Distance == 1)
            {
                // Run-length fill: the match is a single repeated byte.
                Window.AsSpan(dest, len).Fill(Window[src]);
            }
            else if (Distance >= Length)
            {
                // Source and destination ranges do not overlap, so a bulk copy
                // (which lowers to memmove) is safe and fast.
                Window.AsSpan(src, len).CopyTo(Window.AsSpan(dest, len));
            }
            else
            {
                // Overlapping match: bytes must be produced in order so the
                // pattern propagates. Sequential assignments read positions that
                // were already written, matching the byte-by-byte semantics while
                // letting the JIT process 8 bytes per iteration.
                while (len >= 8)
                {
                    Window[dest] = Window[src];
                    Window[dest + 1] = Window[src + 1];
                    Window[dest + 2] = Window[src + 2];
                    Window[dest + 3] = Window[src + 3];
                    Window[dest + 4] = Window[src + 4];
                    Window[dest + 5] = Window[src + 5];
                    Window[dest + 6] = Window[src + 6];
                    Window[dest + 7] = Window[src + 7];
                    src += 8;
                    dest += 8;
                    len -= 8;
                }
                while (len-- > 0)
                {
                    Window[dest++] = Window[src++];
                }
            }
        }
        else
        {
            while (Length-- > 0) // Slow copying with all possible precautions.
            {
                Window[UnpPtr] = Window[SrcPtr++ & MaxWinMask];
                // We need to have masked UnpPtr after quit from loop, so it must not
                // be replaced with 'Window[UnpPtr++ & MaxWinMask]'
                UnpPtr = (UnpPtr + 1) & MaxWinMask;
            }
        }
    }

    private uint DecodeNumber(BitInput Inp, DecodeTable Dec)
    {
        // Left aligned 15 bit length raw bit field.
        var BitField = Inp.getbits() & 0xfffe;

        if (BitField < Dec.DecodeLen[Dec.QuickBits])
        {
            var Code = BitField >> (int)(16 - Dec.QuickBits);
            Inp.addbits(Dec.QuickLen[Code]);
            return Dec.QuickNum[Code];
        }

        // Detect the real bit length for current code.
        uint Bits = 15;
        for (var I = Dec.QuickBits + 1; I < 15; I++)
        {
            if (BitField < Dec.DecodeLen[I])
            {
                Bits = I;
                break;
            }
        }

        Inp.addbits(Bits);

        // Calculate the distance from the start code for current bit length.
        var Dist = BitField - Dec.DecodeLen[Bits - 1];

        // Start codes are left aligned, but we need the normal right aligned
        // number. So we shift the distance to the right.
        Dist >>= (int)(16 - Bits);

        // Now we can calculate the position in the code list. It is the sum
        // of first position for current bit length and right aligned distance
        // between our bit field and start code for current bit length.
        var Pos = Dec.DecodePos[Bits] + Dist;

        // Out of bounds safety check required for damaged archives.
        if (Pos >= Dec.MaxNum)
        {
            Pos = 0;
        }

        // Convert the position in the code list to position in alphabet
        // and return it.
        return Dec.DecodeNum[Pos];
    }

    private uint SlotToLength(BitInput Inp, uint Slot)
    {
        uint LBits,
            Length = 2;
        if (Slot < 8)
        {
            LBits = 0;
            Length += Slot;
        }
        else
        {
            LBits = (Slot / 4) - 1;
            Length += (4 | (Slot & 3)) << (int)LBits;
        }

        if (LBits > 0)
        {
            Length += Inp.getbits() >> (int)(16 - LBits);
            Inp.addbits(LBits);
        }
        return Length;
    }
}
