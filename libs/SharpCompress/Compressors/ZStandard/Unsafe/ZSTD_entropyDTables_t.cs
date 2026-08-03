using System.Runtime.CompilerServices;

namespace SharpCompress.Compressors.ZStandard.Unsafe;

public unsafe struct ZSTD_entropyDTables_t
{
    /* Note : Space reserved for FSE Tables */
    public _LLTable_e__FixedBuffer LLTable;

    /* is also used as temporary workspace while building hufTable during DDict creation */
    public _OFTable_e__FixedBuffer OFTable;

    /* and therefore must be at least HUF_DECOMPRESS_WORKSPACE_SIZE large */
    public _MLTable_e__FixedBuffer MLTable;

    /* can accommodate HUF_decompress4X */
    public fixed uint hufTable[4097];
    public fixed uint rep[3];
    public fixed uint workspace[157];

    [InlineArray(513)]
    public unsafe struct _LLTable_e__FixedBuffer
    {
        public ZSTD_seqSymbol e0;
    }

    [InlineArray(257)]
    public unsafe struct _OFTable_e__FixedBuffer
    {
        public ZSTD_seqSymbol e0;
    }

    [InlineArray(513)]
    public unsafe struct _MLTable_e__FixedBuffer
    {
        public ZSTD_seqSymbol e0;
    }
}
