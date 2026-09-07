using System.Xml;

namespace NzbWebDAV.Models.Nzb;

public class NzbDocument
{
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Ignore
    };

    public Dictionary<string, string> Metadata { get; } = new();

    public List<NzbFile> Files { get; } = [];

    public static Task<NzbDocument> LoadAsync(Stream stream, CancellationToken ct = default)
        => LoadAsync(stream, null, ct);

    internal static async Task<NzbDocument> LoadAsync(Stream stream, NzbReadOptions? options, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var reservation = options?.ReserveReader();
            var document = new NzbDocument();
            var settings = XmlSettings.Clone();
            if (options is not null)
                settings.MaxCharactersInDocument = options.MaxXmlCharacters;
            using var reader = XmlReader.Create(stream, settings);

            // XmlReader.ReadAsync doesn't take a token; check between reads so a
            // cancelled queue worker stops promptly once the current read returns.
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.Name)
                {
                    case "head":
                        await ReadHeadAsync(reader, document.Metadata, options, ct).ConfigureAwait(false);
                        break;
                    case "file":
                        var file = await ReadFileAsync(reader, options, document.Files.Count, ct).ConfigureAwait(false);
                        document.Files.Add(file);
                        break;
                }
            }

            return document;
        }
        catch (OperationCanceledException)
        {
            // Cancellation must stay cancellation; only malformed XML maps to
            // InvalidDataException (which finalizes as failed history).
            throw;
        }
        catch (XmlException e)
        {
            throw new InvalidDataException("Could not parse the nzb document (malformed nzb)", e);
        }
    }

    private static async Task ReadHeadAsync(
        XmlReader reader,
        Dictionary<string, string> metadata,
        NzbReadOptions? options,
        CancellationToken ct)
    {
        if (reader.IsEmptyElement)
            return;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (reader is { NodeType: XmlNodeType.EndElement, Name: "head" })
                break;

            if (reader is { NodeType: XmlNodeType.Element, Name: "meta" })
            {
                var type = reader.GetAttribute("type") ?? string.Empty;
                var value = await ReadTextAsync(reader, options?.MaxMetadataLength, ct).ConfigureAwait(false);
                options?.AddMetadata(type, value);
                metadata.Add(type, value);

                // ReadElementContentAsStringAsync advances the reader - continue to check current position
                continue;
            }

            // Only read if we haven't processed an element that advanced us
            if (!await reader.ReadAsync().ConfigureAwait(false))
                break;
        }
    }

    private static async Task<NzbFile> ReadFileAsync(XmlReader reader, NzbReadOptions? options, int count, CancellationToken ct)
    {
        var subject = reader.GetAttribute("subject") ?? string.Empty;
        options?.AddFile(count, subject);
        var file = new NzbFile
        {
            Subject = subject
        };

        if (reader.IsEmptyElement)
            return file;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (reader is { NodeType: XmlNodeType.EndElement, Name: "file" })
                break;

            if (reader is { NodeType: XmlNodeType.Element, Name: "segments" })
            {
                await ReadSegmentsAsync(reader, file, options, ct).ConfigureAwait(false);
            }
        }

        file.CanonicalizeSegments();
        return file;
    }

    private static async Task ReadSegmentsAsync(XmlReader reader, NzbFile file, NzbReadOptions? options, CancellationToken ct)
    {
        if (reader.IsEmptyElement)
            return;

        while (true)
        {
            if (reader is { NodeType: XmlNodeType.EndElement, Name: "segments" })
                break;

            if (reader is { NodeType: XmlNodeType.Element, Name: "segment" })
            {
                ct.ThrowIfCancellationRequested();
                var bytesAttr = reader.GetAttribute("bytes");
                var numberAttr = reader.GetAttribute("number");
                var messageId = (await ReadTextAsync(reader, options?.MaxMessageIdLength, ct).ConfigureAwait(false)).Trim();
                options?.AddSegment(messageId);
                var segment = new NzbSegment
                {
                    Bytes = long.TryParse(bytesAttr, out var bytes) ? bytes : 0,
                    Number = int.TryParse(numberAttr, out var number) ? number : null,
                    MessageId = messageId
                };
                file.Segments.Add(segment);

                // ReadElementContentAsStringAsync advances the reader - continue to check current position
                continue;
            }

            // Only read if we haven't processed an element that advanced us
            if (!await reader.ReadAsync().ConfigureAwait(false))
                break;
        }
    }

    private static async Task<string> ReadTextAsync(XmlReader reader, int? limit, CancellationToken ct)
    {
        if (limit is null)
            return await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
        if (reader.IsEmptyElement)
        {
            await reader.ReadAsync().ConfigureAwait(false);
            return string.Empty;
        }

        var buffer = new char[limit.Value + 1];
        var length = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                await reader.ReadAsync().ConfigureAwait(false);
                return new string(buffer, 0, length);
            }
            if (reader.NodeType is XmlNodeType.Comment or XmlNodeType.ProcessingInstruction)
                continue;
            if (reader.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace))
                throw new InvalidDataException("NZB text contains nested elements.");
            int read;
            while ((read = await reader.ReadValueChunkAsync(buffer, length, buffer.Length - length).ConfigureAwait(false)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                length += read;
                if (length > limit)
                    throw new InvalidDataException("NZB text exceeds repair parsing limits.");
            }
        }

        throw new InvalidDataException("Truncated NZB text.");
    }
}
