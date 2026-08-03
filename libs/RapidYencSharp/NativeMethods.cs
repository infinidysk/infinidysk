using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RapidYencSharp;

public enum RapidYencDecoderState
{
    RYDEC_STATE_CRLF = 0,
    RYDEC_STATE_EQ = 1,
    RYDEC_STATE_CR = 2,
    RYDEC_STATE_NONE = 3,
    RYDEC_STATE_CRLFDT = 4,
    RYDEC_STATE_CRLFDTCR = 5,
    RYDEC_STATE_CRLFEQ = 6
}

public enum RapidYencDecoderEnd
{
    RYDEC_END_NONE = 0,
    RYDEC_END_CONTROL = 1,
    RYDEC_END_ARTICLE = 2
}

/// <summary>
/// P/Invoke declarations for the rapidyenc native library
/// </summary>
internal static partial class NativeMethods
{
    private const string LibraryName = "rapidyenc";

    static NativeMethods()
    {
        // Try to load the native library
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, (libraryName, assembly, searchPath) =>
        {
            if (libraryName == LibraryName)
            {
                string? explicitPath = Environment.GetEnvironmentVariable("RAPIDYENC_LIBRARY_PATH");
                if (!string.IsNullOrWhiteSpace(explicitPath) &&
                    NativeLibrary.TryLoad(explicitPath, out var explicitHandle))
                {
                    return explicitHandle;
                }

                // Try common library names
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (NativeLibrary.TryLoad("librapidyenc.so", assembly, searchPath, out var handle))
                        return handle;
                    if (NativeLibrary.TryLoad("rapidyenc", assembly, searchPath, out handle))
                        return handle;
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (NativeLibrary.TryLoad("rapidyenc.dll", assembly, searchPath, out var handle))
                        return handle;
                    if (NativeLibrary.TryLoad("rapidyenc", assembly, searchPath, out handle))
                        return handle;
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    if (NativeLibrary.TryLoad("librapidyenc.dylib", assembly, searchPath, out var handle))
                        return handle;
                    if (NativeLibrary.TryLoad("rapidyenc", assembly, searchPath, out handle))
                        return handle;
                }
            }
            return IntPtr.Zero;
        });
    }

    // LibraryImport stubs are generated through a nested helper type. Calling
    // this managed method first guarantees that this type's resolver has run.
    internal static void EnsureResolverRegistered()
    {
    }

    // Version
    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int rapidyenc_version();

    // Kernel constants
    public const int RYKERN_GENERIC = 0;
    public const int RYKERN_SSE2 = 0x100;
    public const int RYKERN_SSSE3 = 0x200;
    public const int RYKERN_AVX = 0x381;
    public const int RYKERN_AVX2 = 0x403;
    public const int RYKERN_VBMI2 = 0x603;
    public const int RYKERN_NEON = 0x1000;
    public const int RYKERN_RVV = 0x10000;
    public const int RYKERN_PCLMUL = 0x340;
    public const int RYKERN_VPCLMUL = 0x440;
    public const int RYKERN_ARMCRC = 8;
    public const int RYKERN_ARMPMULL = 0x48;
    public const int RYKERN_ZBC = 16;

    // Encode functions
    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_encode_init")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void rapidyenc_encode_init();

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_encode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint rapidyenc_encode(
        IntPtr src,
        IntPtr dest,
        nuint src_length);

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_encode_ex")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint rapidyenc_encode_ex(
        int line_size,
        ref int column,
        IntPtr src,
        IntPtr dest,
        nuint src_length,
        int is_end);

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_encode_ex")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint rapidyenc_encode_ex_without_column(
        int line_size,
        IntPtr column,
        IntPtr src,
        IntPtr dest,
        nuint src_length,
        int is_end);

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_encode_kernel")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int rapidyenc_encode_kernel();

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_encode_max_length")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint rapidyenc_encode_max_length(nuint length, int line_size);

    // Decode functions
    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_decode_init")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void rapidyenc_decode_init();

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_decode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint rapidyenc_decode(
        IntPtr src,
        IntPtr dest,
        nuint src_length);

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_decode_ex")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint rapidyenc_decode_ex(
        int is_raw,
        IntPtr src,
        IntPtr dest,
        nuint src_length,
        ref RapidYencDecoderState state);

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_decode_ex")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint rapidyenc_decode_ex_without_state(
        int is_raw,
        IntPtr src,
        IntPtr dest,
        nuint src_length,
        IntPtr state);

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_decode_incremental")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial RapidYencDecoderEnd rapidyenc_decode_incremental(
        ref IntPtr src,
        ref IntPtr dest,
        nuint src_length,
        ref RapidYencDecoderState state);

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_decode_kernel")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int rapidyenc_decode_kernel();

    // CRC32 functions
    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_crc_init")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void rapidyenc_crc_init();

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_crc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint rapidyenc_crc(
        IntPtr src,
        nuint src_length,
        uint init_crc);

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_crc_combine")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint rapidyenc_crc_combine(uint crc1, uint crc2, ulong length2);

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_crc_zeros")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint rapidyenc_crc_zeros(uint init_crc, ulong length);

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_crc_unzero")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint rapidyenc_crc_unzero(uint init_crc, ulong length);

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_crc_multiply")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint rapidyenc_crc_multiply(uint a, uint b);

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_crc_2pow")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint rapidyenc_crc_2pow(long n);

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_crc_256pow")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint rapidyenc_crc_256pow(ulong n);

    [LibraryImport(LibraryName, EntryPoint = "rapidyenc_crc_kernel")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int rapidyenc_crc_kernel();
}
