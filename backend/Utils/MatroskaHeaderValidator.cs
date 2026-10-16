namespace NzbWebDAV.Utils;

/// <summary>
/// Checks the EBML header of a Matroska/WebM prefix for the declarations ffmpeg refuses to
/// open (its demuxer reports them as unsupported features). Everything else ffmpeg only
/// warns about or accepts, so anything this cannot parse is accepted rather than rejected.
/// </summary>
internal static class MatroskaHeaderValidator
{
    private const uint EbmlReadVersionId = 0x42F7;
    private const uint EbmlMaxIdLengthId = 0x42F2;
    private const uint EbmlMaxSizeLengthId = 0x42F3;

    internal static string? GetError(ReadOnlySpan<byte> data)
    {
        var offset = 4; // The caller has already recognized the EBML magic.
        if (!TryReadVint(data, ref offset, stripMarker: true, out var headerSize))
            return null;
        var headerEnd = (ulong)offset + headerSize;
        while ((ulong)offset < headerEnd)
        {
            if (!TryReadVint(data, ref offset, stripMarker: false, out var id)
                || !TryReadVint(data, ref offset, stripMarker: true, out var size)
                || (size > 8 && id is EbmlReadVersionId or EbmlMaxIdLengthId or EbmlMaxSizeLengthId)
                || size > (ulong)(data.Length - offset))
                return null;

            if (id is EbmlReadVersionId or EbmlMaxIdLengthId or EbmlMaxSizeLengthId)
            {
                ulong value = 0;
                foreach (var item in data.Slice(offset, (int)size)) value = (value << 8) | item;
                if (id == EbmlReadVersionId && value > 1)
                    return $"Unsupported EBMLReadVersion {value}.";
                if (id == EbmlMaxIdLengthId && value > 4)
                    return $"Unsupported EBMLMaxIDLength {value}.";
                if (id == EbmlMaxSizeLengthId && value > 8)
                    return $"Unsupported EBMLMaxSizeLength {value}.";
            }

            offset += (int)size;
        }

        return null;
    }

    private static bool TryReadVint(ReadOnlySpan<byte> data, ref int offset, bool stripMarker, out ulong value)
    {
        value = 0;
        if (offset >= data.Length || data[offset] == 0) return false;
        var mask = 0x80;
        var length = 1;
        while ((data[offset] & mask) == 0) { mask >>= 1; length++; }
        if (length > data.Length - offset) return false;
        value = stripMarker ? (ulong)(data[offset] & (mask - 1)) : data[offset];
        offset++;
        for (var i = 1; i < length; i++) value = (value << 8) | data[offset++];
        return true;
    }
}
