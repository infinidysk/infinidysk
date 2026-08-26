using System.Xml;
using NzbWebDAV.Api.Errors;

namespace NzbWebDAV.Models.Nzb;

public static class NzbInputValidator
{
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false,
    };

    /// <summary>
    /// Walks NZB XML incrementally, enforces <paramref name="limits"/>, and
    /// returns the sum of valid segment byte counts. Does not echo message IDs
    /// or raw XML in error text. Cancellation is observed per file and per
    /// segment so a cancelled submission stops promptly on huge documents.
    /// </summary>
    public static long ValidateAndSumSegmentBytes(
        Stream stream,
        NzbInputLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(limits);

        if (stream.CanSeek)
        {
            var remaining = stream.Length - stream.Position;
            if (remaining > limits.MaxXmlBytes)
            {
                Throw("nzb", "The NZB document exceeds the maximum allowed size.");
            }
        }

        var errors = new ValidationErrors();
        long totalBytes = 0;
        var fileCount = 0;
        var totalSegments = 0;
        using var counting = new CountingStream(stream, limits.MaxXmlBytes, () =>
        {
            errors.Add("nzb", "The NZB document exceeds the maximum allowed size.");
            errors.ThrowIfAny();
        });
        using var reader = XmlReader.Create(counting, XmlSettings);

        try
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                if (reader.LocalName == "file")
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    fileCount++;
                    if (fileCount > limits.MaxFiles)
                    {
                        errors.Add(
                            "nzb",
                            $"The NZB document contains too many files ({fileCount}; limit {limits.MaxFiles}).");
                        errors.ThrowIfAny();
                    }

                    var subject = reader.GetAttribute("subject") ?? string.Empty;
                    if (subject.Length > limits.MaxSubjectLength)
                    {
                        errors.Add(
                            "nzb",
                            $"An NZB file subject exceeds the maximum length ({limits.MaxSubjectLength}).");
                    }

                    totalBytes = AddSegmentBytesOrThrow(
                        totalBytes,
                        ReadFileSegments(reader, limits, errors, ref totalSegments, cancellationToken),
                        errors);
                }
            }
        }
        catch (XmlException)
        {
            errors.Add("nzb", "The NZB document is not valid XML.");
            errors.ThrowIfAny();
        }

        errors.ThrowIfAny();
        return totalBytes;
    }

    private static long ReadFileSegments(
        XmlReader reader,
        NzbInputLimits limits,
        ValidationErrors errors,
        ref int totalSegments,
        CancellationToken cancellationToken)
    {
        long fileBytes = 0;
        var seenNumbers = new HashSet<int>();
        if (reader.IsEmptyElement)
            return 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader is { NodeType: XmlNodeType.EndElement, LocalName: "file" })
                break;

            if (reader is { NodeType: XmlNodeType.Element, LocalName: "segment" })
            {
                totalSegments++;
                if (totalSegments > limits.MaxTotalSegments)
                {
                    errors.Add(
                        "nzb",
                        $"The NZB document contains too many segments ({totalSegments}; limit {limits.MaxTotalSegments}).");
                    errors.ThrowIfAny();
                }

                var bytesAttr = reader.GetAttribute("bytes");
                if (!long.TryParse(bytesAttr, out var bytes) || bytes < 0)
                    errors.Add("nzb", "An NZB segment has an invalid byte count.");
                else
                    fileBytes = AddSegmentBytesOrThrow(fileBytes, bytes, errors);

                var numberAttr = reader.GetAttribute("number");
                if (numberAttr is not null)
                {
                    if (!int.TryParse(numberAttr, out var number) || number < 1)
                        errors.Add("nzb", "An NZB segment has an invalid number.");
                    else if (!seenNumbers.Add(number))
                        errors.Add("nzb", "An NZB file contains duplicate segment numbers.");
                }

                var messageId = reader.ReadElementContentAsString().Trim();
                if (messageId.Length == 0)
                    errors.Add("nzb", "An NZB segment is missing a message ID.");
                else if (messageId.Length > limits.MaxMessageIdLength)
                    errors.Add("nzb", "An NZB segment message ID exceeds the maximum length.");

                continue;
            }

            if (!reader.Read())
                break;
        }

        return fileBytes;
    }

    private static long AddSegmentBytesOrThrow(long total, long bytes, ValidationErrors errors)
    {
        try
        {
            return checked(total + bytes);
        }
        catch (OverflowException)
        {
            errors.Add(
                "nzb",
                "The NZB document's total segment byte count exceeds the maximum supported size.");
            errors.ThrowIfAny();
            return 0;
        }
    }

    /// <summary>
    /// The size-limit error thrown both while streaming the decompressed
    /// document to disk (via a bounded read stream) and while validating the
    /// committed blob, so SAB clients always get the same 4xx sentence.
    /// </summary>
    internal static ApiValidationException CreateSizeLimitException() =>
        new(
            new Dictionary<string, string[]>
            {
                ["nzb"] = ["The NZB document exceeds the maximum allowed size."],
            },
            "The NZB document exceeds the maximum allowed size.");

    private static void Throw(string field, string message)
    {
        var errors = new ValidationErrors();
        errors.Add(field, message);
        errors.ThrowIfAny();
    }

    private sealed class CountingStream(Stream inner, int maxBytes, Action onLimit) : Stream
    {
        public int BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Add(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            Add(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Add(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Add(read);
            return read;
        }

        private void Add(int read)
        {
            if (read <= 0) return;
            BytesRead += read;
            if (BytesRead > maxBytes)
                onLimit();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
