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

    public static async Task<NzbDocument> LoadAsync(Stream stream, CancellationToken ct = default)
    {
        try
        {
            var document = new NzbDocument();
            using var reader = XmlReader.Create(stream, XmlSettings);

            // XmlReader.ReadAsync doesn't take a token; check between reads so a
            // cancelled queue worker stops promptly once the current read returns.
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.Name)
                {
                    case "head":
                        await ReadHeadAsync(reader, document.Metadata, ct).ConfigureAwait(false);
                        break;
                    case "file":
                        var file = await ReadFileAsync(reader, ct).ConfigureAwait(false);
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
                var value = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                metadata.Add(type, value);

                // ReadElementContentAsStringAsync advances the reader - continue to check current position
                continue;
            }

            // Only read if we haven't processed an element that advanced us
            if (!await reader.ReadAsync().ConfigureAwait(false))
                break;
        }
    }

    private static async Task<NzbFile> ReadFileAsync(XmlReader reader, CancellationToken ct)
    {
        var file = new NzbFile
        {
            Subject = reader.GetAttribute("subject") ?? string.Empty
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
                await ReadSegmentsAsync(reader, file, ct).ConfigureAwait(false);
            }
        }

        file.CanonicalizeSegments();
        return file;
    }

    private static async Task ReadSegmentsAsync(XmlReader reader, NzbFile file, CancellationToken ct)
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
                var segment = new NzbSegment
                {
                    Bytes = long.TryParse(bytesAttr, out var bytes) ? bytes : 0,
                    Number = int.TryParse(numberAttr, out var number) ? number : null,
                    MessageId = (await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)).Trim()
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
}
