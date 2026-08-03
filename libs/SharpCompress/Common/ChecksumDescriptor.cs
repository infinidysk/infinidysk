namespace SharpCompress.Common;

internal enum ChecksumKind
{
    Crc32,
    Crc32NoFinalXor,
    Crc16Arc,
}

internal readonly record struct ChecksumDescriptor(
    ChecksumKind Kind,
    long ExpectedValue,
    bool IsAvailable
);
