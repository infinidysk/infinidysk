using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpCompress.Compressors.ZStandard;

public static unsafe class UnsafeHelper
{
    public static void* PoisonMemory(void* destination, ulong size)
    {
        memset(destination, 0xCC, (uint)size);
        return destination;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* malloc(ulong size)
    {
        var ptr = NativeMemory.Alloc((nuint)size);
        return ptr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* calloc(ulong num, ulong size)
    {
        return NativeMemory.AllocZeroed((nuint)num, (nuint)size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void memcpy(void* destination, void* source, uint size) =>
        System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned(destination, source, size);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void memset(void* memPtr, byte val, uint size) =>
        System.Runtime.CompilerServices.Unsafe.InitBlockUnaligned(memPtr, val, size);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void free(void* ptr)
    {
        NativeMemory.Free(ptr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T* GetArrayPointer<T>(T[] array)
        where T : unmanaged
    {
        var size = (uint)(sizeof(T) * array.Length);
        // AllocateTypeAssociatedMemory links static data lifetime to this assembly so
        // unloadable AssemblyLoadContexts do not leak the copied blocks.
        var destination = (T*)
            RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(UnsafeHelper), (int)size);
        fixed (void* source = &array[0])
        {
            System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned(destination, source, size);
        }

        return destination;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void assert(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new ArgumentException(message ?? "assert failed");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void memmove(void* destination, void* source, ulong size) =>
        Buffer.MemoryCopy(source, destination, size, size);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int memcmp(void* buf1, void* buf2, ulong size)
    {
        assert(size <= int.MaxValue);
        var intSize = (int)size;
        return new ReadOnlySpan<byte>(buf1, intSize).SequenceCompareTo(
            new ReadOnlySpan<byte>(buf2, intSize)
        );
    }
}
