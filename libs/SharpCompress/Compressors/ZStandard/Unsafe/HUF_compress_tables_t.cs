using System.Runtime.CompilerServices;

namespace SharpCompress.Compressors.ZStandard.Unsafe;

public unsafe struct HUF_compress_tables_t
{
    public fixed uint count[256];
    public _CTable_e__FixedBuffer CTable;
    public _wksps_e__Union wksps;

    [InlineArray(257)]
    public unsafe struct _CTable_e__FixedBuffer
    {
        public nuint e0;
    }
}
