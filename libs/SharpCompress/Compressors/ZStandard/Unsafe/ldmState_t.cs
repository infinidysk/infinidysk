using System.Runtime.CompilerServices;

namespace SharpCompress.Compressors.ZStandard.Unsafe;

public unsafe struct ldmState_t
{
    /* State for the window round buffer management */
    public ZSTD_window_t window;
    public ldmEntry_t* hashTable;
    public uint loadedDictEnd;

    /* Next position in bucket to insert entry */
    public byte* bucketOffsets;
    public _splitIndices_e__FixedBuffer splitIndices;
    public _matchCandidates_e__FixedBuffer matchCandidates;

    [InlineArray(64)]
    public unsafe struct _splitIndices_e__FixedBuffer
    {
        public nuint e0;
    }

    [InlineArray(64)]
    public unsafe struct _matchCandidates_e__FixedBuffer
    {
        public ldmMatchCandidate_t e0;
    }
}
