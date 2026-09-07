using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Tests.Fakes;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Services.Repair;

internal sealed class Par2RepairTestReleaseBuilder(ConfigManager config, string configRoot)
{
    internal sealed record SourceFile(string Name, byte[] Data, int[] Sizes,
        int[]? Missing = null, string? Subject = null, byte[]? FileHashOverride = null);
    internal sealed record PostedFile(SourceFile Source, string[] Ids, LongRange[] Ranges);

    internal async Task<SeededRelease> BuildAsync(
        IReadOnlyList<SourceFile> files, uint[] recoveryExponents,
        DavItem.ItemSubType subtype = DavItem.ItemSubType.NzbFile,
        bool trustedRanges = true, bool pendingParts = false,
        Func<int, int, byte[], Stream>? streamFactory = null,
        (byte[] Index, byte[] Recovery)? parity = null,
        bool obfuscatedParity = false,
        IReadOnlyList<(string Name, byte[] Bytes)>? additionalParity = null,
        long? recoveryFileSize = null)
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        var hashOverrides = files.Where(file => file.FileHashOverride is not null)
            .ToDictionary(file => file.Name, file => file.FileHashOverride!, StringComparer.Ordinal);
        var (indexBytes, recoveryBytes) = parity ?? Par2TestEncoder.EncodeSet(
            files.Select(file => (file.Name, file.Data)).ToArray(), 4096, recoveryExponents, hashOverrides);
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var headers = new Dictionary<string, UsenetYencHeader>(StringComparer.Ordinal);
        var sourcePositions = new Dictionary<string, (int File, int Segment)>(StringComparer.Ordinal);
        var posted = new List<PostedFile>();
        var nzb = new XElement("nzb");
        for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            var file = files[fileIndex];
            var ids = new string[file.Sizes.Length];
            var ranges = new LongRange[file.Sizes.Length];
            var segments = new XElement("segments");
            var offset = 0;
            for (var index = 0; index < file.Sizes.Length; index++)
            {
                var id = $"content-{token}-{fileIndex}-{index}@test";
                ids[index] = id;
                ranges[index] = LongRange.FromStartAndSize(offset, file.Sizes[index]);
                if (file.Missing?.Contains(index) != true)
                    payloads[id] = file.Data.AsSpan(offset, file.Sizes[index]).ToArray();
                headers[id] = Header(file.Name, file.Data.Length, index, file.Sizes.Length, ranges[index]);
                sourcePositions[id] = (fileIndex, index);
                segments.Add(new XElement("segment", new XAttribute("bytes", file.Sizes[index]), new XAttribute("number", index + 1), id));
                offset += file.Sizes[index];
            }
            if (offset != file.Data.Length) throw new ArgumentException("Segment sizes must cover the file.", nameof(files));
            posted.Add(new PostedFile(file, ids, ranges));
            nzb.Add(new XElement("file", new XAttribute("subject", $"\"{file.Subject ?? file.Name}\" yEnc"), segments));
        }

        AddParity("aaa-index-" + token + "@test", obfuscatedParity ? "unknown-index" : "release.par2", indexBytes);
        AddParity("zzz-volume-" + token + "@test", obfuscatedParity ? "unknown-recovery" : "release.vol00+08.par2", recoveryBytes, recoveryFileSize);
        foreach (var (entry, index) in (additionalParity ?? []).Select((entry, index) => (entry, index)))
            AddParity($"000-extra-{index}-{token}@test", entry.Name, entry.Bytes);
        var fake = new FakeNntpClient(payloads, useCachedYencStreams: true, yencHeaders: headers,
            decodedStreamFactory: (id, bytes) => sourcePositions.TryGetValue(id, out var position) && streamFactory is not null
                ? streamFactory(position.File, position.Segment, bytes) : new MemoryStream(bytes, writable: false));

        var nzbBlobId = Guid.NewGuid();
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(nzb.ToString(SaveOptions.DisableFormatting))))
            await BlobStore.WriteBlob(nzbBlobId, stream);
        var fileBlobId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var target = posted[^1];
        if (subtype == DavItem.ItemSubType.NzbFile)
            await BlobStore.WriteBlob(fileBlobId, new DavNzbFile
            {
                Id = itemId, SegmentIds = target.Ids, SegmentByteRanges = target.Ranges, SegmentByteRangesTrusted = trustedRanges,
            });
        else if (subtype == DavItem.ItemSubType.RarFile)
            await BlobStore.WriteBlob(fileBlobId, new DavRarFile
            {
                Id = itemId,
                RarParts = posted.Select(file => new DavRarFile.RarPart
                {
                    SegmentIds = file.Ids, PartSize = file.Source.Data.Length, ByteCount = file.Source.Data.Length,
                    SegmentByteRanges = file.Ranges, SegmentByteRangesTrusted = trustedRanges,
                }).ToArray(),
            });
        else
            await BlobStore.WriteBlob(fileBlobId, new DavMultipartFile
            {
                Id = itemId,
                Metadata = new DavMultipartFile.Meta
                {
                    IsLazy = pendingParts,
                    FileParts = posted.Take(pendingParts ? 1 : posted.Count).Select(file => new DavMultipartFile.FilePart
                    {
                        SegmentIds = file.Ids, SegmentByteRanges = file.Ranges, SegmentByteRangesTrusted = trustedRanges,
                        SegmentIdByteRange = LongRange.FromStartAndSize(0, file.Source.Data.Length),
                        FilePartByteRange = LongRange.FromStartAndSize(0, file.Source.Data.Length),
                    }).ToArray(),
                    PendingParts = pendingParts ? posted.Skip(1).Select(file => new DavMultipartFile.PendingPart
                    {
                        SegmentIds = file.Ids, EstimatedDataSize = file.Source.Data.Length,
                        SegmentIdByteRange = LongRange.FromStartAndSize(0, file.Source.Data.Length),
                    }).ToArray() : [],
                },
            });

        var item = DavItem.New(itemId, DavItem.ContentFolder,
            subtype == DavItem.ItemSubType.NzbFile ? target.Source.Name : "release-" + token + ".mkv",
            subtype == DavItem.ItemSubType.NzbFile ? target.Source.Data.Length : files.Sum(file => (long)file.Data.Length),
            DavItem.ItemType.UsenetFile, subtype, DateTimeOffset.UtcNow.AddDays(-1), null, null, fileBlobId, nzbBlobId);
        await using (var context = new DavDatabaseContext())
        {
            context.Items.Add(item);
            await context.SaveChangesAsync();
        }
        var patchDir = Path.Join(configRoot, "patches", token);
        var store = new RepairPatchStore(patchDir, 32 * 1024 * 1024);
        await store.EnsureCatalogLoadedAsync(CancellationToken.None);
        var usenet = new UsenetStreamingClient(fake, store);
        return new SeededRelease(item, posted, fake, store, new Par2RepairService(config, usenet, store), usenet, patchDir);

        void AddParity(string id, string name, byte[] bytes, long? declaredSize = null)
        {
            payloads[id] = bytes;
            headers[id] = Header(name, bytes.Length, 0, 1, LongRange.FromStartAndSize(0, bytes.Length));
            if (declaredSize is { } size) headers[id].FileSize = size;
            nzb.Add(new XElement("file", new XAttribute("subject", $"\"{name}\" yEnc"),
                new XElement("segments", new XElement("segment", new XAttribute("bytes", bytes.Length), new XAttribute("number", 1), id))));
        }
    }

    private static UsenetYencHeader Header(string name, int length, int index, int total, LongRange range) => new()
    {
        FileName = name, FileSize = length, PartNumber = index + 1, TotalParts = total,
        PartOffset = range.StartInclusive, PartSize = range.Count, LineLength = 128,
    };

    internal sealed class SeededRelease(DavItem item, IReadOnlyList<PostedFile> files, FakeNntpClient fake,
        RepairPatchStore store, Par2RepairService service, UsenetStreamingClient usenet, string patchDirectory) : IAsyncDisposable
    {
        public DavItem Item { get; } = item;
        public IReadOnlyList<PostedFile> Files { get; } = files;
        public string[] ContentSegmentIds => Files[^1].Ids;
        public FakeNntpClient Fake { get; } = fake;
        public RepairPatchStore Store { get; } = store;
        public Par2RepairService Service { get; } = service;
        public UsenetStreamingClient Usenet { get; } = usenet;
        public string PatchDirectory { get; } = patchDirectory;

        public ValueTask DisposeAsync()
        {
            Service.Dispose();
            Usenet.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}