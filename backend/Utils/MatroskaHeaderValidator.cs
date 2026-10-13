namespace NzbWebDAV.Utils;

/// <summary>Checks the bounded EBML header prefix, without interpreting tracks or downloading the media.</summary>
internal static class MatroskaHeaderValidator
{
    // Matroska constraints: RFC 9559 section 4.3; EBML encoding: RFC 8794.
    internal static string? GetError(ReadOnlySpan<byte> data, long fileSize)
    {
        var offset = 4; // The caller has already recognized the EBML magic.
        if (!TryReadVint(data, ref offset, stripMarker: true, 8, out var headerSize, out var unknown))
            return "Invalid EBML header size.";
        if (unknown || headerSize > (ulong)Math.Max(0, fileSize - offset))
            return "EBML header extends beyond the media file.";
        var headerEnd = (ulong)offset + headerSize;
        var docTypeSeen = false;
        while ((ulong)offset < headerEnd)
        {
            // A large, valid header may extend beyond the bounded probe. Inspect
            // every complete field available, but do not reject it for that alone.
            if (offset >= data.Length) return null;
            if (headerEnd > (ulong)data.Length && IsPartialVint(data, offset, 4)) return null;
            if (!TryReadVint(data, ref offset, stripMarker: false, 4, out var id, out _))
                return "Invalid EBML header element ID.";
            if (headerEnd > (ulong)data.Length && IsPartialVint(data, offset, 8)) return null;
            if (!TryReadVint(data, ref offset, stripMarker: true, 8, out var size, out unknown))
                return "Invalid EBML header element size.";
            if (unknown || (ulong)offset > headerEnd || size > headerEnd - (ulong)offset)
                return "EBML element extends beyond its header.";
            if (size > (ulong)(data.Length - offset)) return null;
            var value = data.Slice(offset, (int)size);
            if (id == 0x4282)
            {
                var terminator = value.IndexOf((byte)0);
                if (terminator >= 0) value = value[..terminator];
                if (docTypeSeen || (!value.SequenceEqual("matroska"u8) && !value.SequenceEqual("webm"u8)))
                    return "Invalid Matroska/WebM document type.";
                docTypeSeen = true;
            }
            else if (id is 0x42F2 or 0x42F3 or 0x42F7)
            {
                if (value.Length is 0 or > 8)
                    return "Invalid EBML header integer.";
                ulong number = 0;
                foreach (var item in value) number = (number << 8) | item;
                if (id == 0x42F2 && number != 4)
                    return "Matroska EBMLMaxIDLength must be 4.";
                if (id == 0x42F3 && number is < 1 or > 8)
                    return "Matroska EBMLMaxSizeLength must be between 1 and 8.";
                if (id == 0x42F7 && number != 1)
                    return "Unsupported EBMLReadVersion.";
            }
            offset += (int)size;
        }
        return docTypeSeen ? null : "Missing Matroska/WebM document type.";
    }

    private static bool IsPartialVint(ReadOnlySpan<byte> data, int offset, int maxLength)
    {
        if (offset >= data.Length) return true;
        if (data[offset] == 0) return false;
        var mask = 0x80;
        var length = 1;
        while ((data[offset] & mask) == 0) { mask >>= 1; length++; }
        return length <= maxLength && length > data.Length - offset;
    }

    private static bool TryReadVint(
        ReadOnlySpan<byte> data, ref int offset, bool stripMarker, int maxLength,
        out ulong value, out bool unknown)
    {
        value = 0;
        unknown = false;
        if (offset >= data.Length || data[offset] == 0) return false;
        var mask = 0x80;
        var length = 1;
        while ((data[offset] & mask) == 0) { mask >>= 1; length++; }
        if (length > maxLength || length > data.Length - offset) return false;
        value = stripMarker ? (ulong)(data[offset] & (mask - 1)) : data[offset];
        offset++;
        for (var i = 1; i < length; i++) value = (value << 8) | data[offset++];
        unknown = stripMarker && value == (1UL << (length * 7)) - 1;
        return true;
    }
}
