using System.Runtime.CompilerServices;

namespace SharpCompress.Compressors.ZStandard.Unsafe;

public unsafe struct HUF_ReadDTableX2_Workspace
{
    public _rankVal_e__FixedBuffer rankVal;
    public fixed uint rankStats[13];
    public fixed uint rankStart0[15];
    public _sortedSymbol_e__FixedBuffer sortedSymbol;
    public fixed byte weightList[256];
    public fixed uint calleeWksp[219];

    [InlineArray(12)]
    public unsafe struct _rankVal_e__FixedBuffer
    {
        public rankValCol_t e0;
    }

    [InlineArray(256)]
    public unsafe struct _sortedSymbol_e__FixedBuffer
    {
        public sortedSymbol_t e0;
    }
}
