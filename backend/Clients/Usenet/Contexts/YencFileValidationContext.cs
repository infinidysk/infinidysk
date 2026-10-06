using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet.Contexts;

internal sealed class YencFileValidationContext : IDisposable
{
    private static readonly byte[] DiagnosticKey = RandomNumberGenerator.GetBytes(32);
    private static readonly AsyncLocal<YencFileValidationContext?> Active = new();
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The borrowed parent scope owns its disposal and must be restored, not disposed.")]
    private readonly YencFileValidationContext? _previous = Active.Value;
    private readonly NzbFile? _file;
    private readonly string[]? _segmentIds;
    private readonly string[][]? _segmentFallbacks;
    private readonly Lazy<Dictionary<string, int>> _positionIndex;
    private readonly bool _deferToPar2Proof;

    private YencFileValidationContext(
        int expectedTotalParts,
        string stage = "Unknown",
        NzbFile? file = null,
        string[]? segmentIds = null,
        string[][]? segmentFallbacks = null,
        bool deferToPar2Proof = false,
        Lazy<Dictionary<string, int>>? positionIndex = null)
    {
        ExpectedTotalParts = expectedTotalParts;
        Stage = stage;
        _file = file;
        _segmentIds = segmentIds;
        _segmentFallbacks = segmentFallbacks;
        _positionIndex = positionIndex ?? new Lazy<Dictionary<string, int>>(CreatePositionIndex);
        _deferToPar2Proof = deferToPar2Proof;
        Active.Value = this;
    }

    public static YencFileValidationContext? Current => Active.Value;
    public static int? CurrentExpectedTotalParts => Current?.ExpectedTotalParts;
    public int ExpectedTotalParts { get; }
    public string Stage { get; }

    public static bool MatchesExpectedFile(UsenetYencHeader header, string? requestedId = null) =>
        Current?._deferToPar2Proof == true
        || CurrentExpectedTotalParts is not { } expectedTotalParts
        || (expectedTotalParts == 1 && header.TotalParts == 0)
        || (header.HasTotalParts == false && header.TotalParts == 0)
        || header.TotalParts == expectedTotalParts
        || (requestedId is not null
            && Current!.GetRequestDetails(requestedId).Position is { } position
            && HasSelfContradictoryTotal(header, position));

    // Some posters obfuscate total/size; a part at its requested ordinal whose own geometry
    // cannot yield that total carries no evidence of belonging to another post.
    internal static bool HasSelfContradictoryTotal(UsenetYencHeader header, int position)
    {
        if (header.PartNumber != position || header.TotalParts <= 0 || header.FileSize <= 0 || header.PartSize <= 0)
            return false;

        long regularPartSize;
        if (position == 1)
        {
            if (header.PartOffset != 0) return false;
            regularPartSize = header.PartSize;
        }
        else
        {
            if (header.PartOffset <= 0 || header.PartOffset % (position - 1L) != 0) return false;
            regularPartSize = header.PartOffset / (position - 1L);
            if (header.PartSize > regularPartSize) return false;
        }

        return (header.FileSize - 1) / regularPartSize + 1 != header.TotalParts;
    }

    public static IDisposable Begin(int expectedTotalParts) =>
        new YencFileValidationContext(expectedTotalParts);

    public static IDisposable BeginSizeProbe(NzbFile file) =>
        new YencFileValidationContext(file.Segments.Count, "SizeProbe", file: file);

    public static IDisposable BeginStreaming(
        string[] segmentIds,
        string[][]? segmentFallbacks,
        Lazy<Dictionary<string, int>>? positionIndex = null) =>
        new YencFileValidationContext(
            segmentIds.Length, "Streaming", segmentIds: segmentIds, segmentFallbacks: segmentFallbacks,
            deferToPar2Proof: Current?._deferToPar2Proof == true
                && ReferenceEquals(Current._segmentIds, segmentIds),
            positionIndex: positionIndex);

    internal static Lazy<Dictionary<string, int>> CreatePositionIndex(
        string[] segmentIds,
        string[][]? segmentFallbacks) =>
        new(() => BuildPositionIndex(segmentIds, segmentFallbacks));

    internal static IDisposable BeginBufferedPar2ProofRead(
        string[] segmentIds,
        string[][]? segmentFallbacks,
        Lazy<Dictionary<string, int>>? positionIndex = null) =>
        new YencFileValidationContext(
            segmentIds.Length, "BufferedPar2ProofRead", segmentIds: segmentIds,
            segmentFallbacks: segmentFallbacks, deferToPar2Proof: true,
            positionIndex: positionIndex);

    public (string? FileAnchor, int? Position, int? NzbNumber) GetRequestDetails(string requestedId)
    {
        if (_file is { Segments.Count: > 0 })
        {
            var anchor = _file.Segments[0].MessageId;
            return _positionIndex.Value.TryGetValue(NormalizeMessageId(requestedId), out var position)
                ? (anchor, position, _file.Segments[position - 1].Number)
                : (anchor, null, null);
        }

        if (_segmentIds is { Length: > 0 })
        {
            return (_segmentIds[0],
                _positionIndex.Value.TryGetValue(NormalizeMessageId(requestedId), out var position) ? position : null,
                null);
        }

        return (null, null, null);
    }

    public void ReportMismatch(string requestedId, string providerKey, int responseCode, UsenetYencHeader header)
    {
        var (fileAnchor, position, nzbNumber) = GetRequestDetails(requestedId);
        var requestedIdKind = position is null ? "Unknown" : IsPrimaryRequest(requestedId, position) ? "Primary" : "Fallback";
        var geometryImpliedTotalParts = GetGeometryImpliedTotalParts(header);
        var fileRef = GetDiagnosticReference(fileAnchor);
        var articleRef = GetDiagnosticReference(requestedId);
        var providerRef = GetDiagnosticReference(providerKey);
        var returnedNameRef = GetDiagnosticReference(header.FileName);
        var key = $"yenc-mismatch/{Stage}/{fileRef}/{articleRef}/{providerRef}/{position}/{nzbNumber}/" +
                  $"{ExpectedTotalParts}/{responseCode}/{returnedNameRef}/{header.PartNumber}/{header.TotalParts}/" +
                  $"{header.FileSize}/{header.PartOffset}/{header.PartSize}/{header.LineLength}";
        ThrottledSegmentWarning.Write(
            key,
            "Rejected yEnc article because its parsed total differs from the active file segment count. " +
            "Stage: {Stage}; FileRef: {FileRef}; ArticleRef: {ArticleRef}; ProviderRef: {ProviderRef}; " +
            "RequestedIdKind: {RequestedIdKind}; RequestedSegmentPosition: {RequestedSegmentPosition}; " +
            "NzbSegmentNumber: {NzbSegmentNumber}; " +
            "ExpectedTotalParts: {ExpectedTotalParts}; ResponseCode: {ResponseCode}; " +
            "GeometryImpliedTotalParts: {GeometryImpliedTotalParts}; " +
            "ReturnedNameRef: {ReturnedNameRef}; ReturnedPartNumber: {ReturnedPartNumber}; " +
            "ReturnedTotalParts: {ReturnedTotalParts}; ReturnedFileSize: {ReturnedFileSize}; " +
            "ReturnedPartOffset: {ReturnedPartOffset}; ReturnedPartSize: {ReturnedPartSize}; " +
            "ReturnedLineLength: {ReturnedLineLength}; MetadataSource: ParsedYencHeader",
            Stage, fileRef, articleRef, providerRef, requestedIdKind, position, nzbNumber, ExpectedTotalParts, responseCode,
            geometryImpliedTotalParts,
            returnedNameRef, header.PartNumber, header.TotalParts, header.FileSize, header.PartOffset,
            header.PartSize, header.LineLength);
    }

    private bool IsPrimaryRequest(string requestedId, int? position)
    {
        if (position is not > 0) return false;
        var index = position.Value - 1;
        if (_file is { } file && index < file.Segments.Count)
            return string.Equals(NormalizeMessageId(file.Segments[index].MessageId), NormalizeMessageId(requestedId),
                StringComparison.Ordinal);
        return _segmentIds is not null && index < _segmentIds.Length
            && string.Equals(NormalizeMessageId(_segmentIds[index]), NormalizeMessageId(requestedId),
                StringComparison.Ordinal);
    }

    private Dictionary<string, int> CreatePositionIndex()
    {
        if (_file is { } file)
            return BuildPositionIndex(file.GetSegmentIds(), file.GetSegmentFallbackIds());
        return BuildPositionIndex(_segmentIds ?? [], _segmentFallbacks);
    }

    private static Dictionary<string, int> BuildPositionIndex(string[] segmentIds, string[][]? segmentFallbacks)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var segmentIndex = 0; segmentIndex < segmentIds.Length; segmentIndex++)
            index.TryAdd(NormalizeMessageId(segmentIds[segmentIndex]), segmentIndex + 1);

        if (segmentFallbacks is not null)
        {
            for (var segmentIndex = 0; segmentIndex < Math.Min(segmentIds.Length, segmentFallbacks.Length); segmentIndex++)
            {
                if (segmentFallbacks[segmentIndex] is not { } fallbackIds) continue;
                foreach (var fallbackId in fallbackIds)
                    index.TryAdd(NormalizeMessageId(fallbackId), segmentIndex + 1);
            }
        }

        return index;
    }

    private static string NormalizeMessageId(string messageId) => new SegmentId(messageId).ToString();

    private static long? GetGeometryImpliedTotalParts(UsenetYencHeader header)
    {
        if (header.FileSize <= 0) return null;
        long regularPartSize;
        if (header.PartNumber == 1 && header.PartOffset == 0 && header.PartSize > 0)
        {
            regularPartSize = header.PartSize;
        }
        else if (header.PartNumber > 1 && header.PartOffset > 0
                 && header.PartOffset % (header.PartNumber - 1L) == 0)
        {
            regularPartSize = header.PartOffset / (header.PartNumber - 1L);
        }
        else
        {
            return null;
        }
        if (regularPartSize <= 0) return null;

        var impliedTotalParts = (header.FileSize - 1) / regularPartSize + 1;
        if (header.PartNumber <= 0 || header.PartNumber > impliedTotalParts)
            return null;

        var remainingFileSize = header.FileSize - header.PartOffset;
        if (remainingFileSize <= 0) return null;
        var expectedPartSize = Math.Min(regularPartSize, remainingFileSize);
        return header.PartSize == expectedPartSize ? impliedTotalParts : null;
    }

    internal static string? GetDiagnosticReference(string? value) => value is null
        ? null
        : Convert.ToHexString(HMACSHA256.HashData(DiagnosticKey, Encoding.UTF8.GetBytes(value)));

    public void Dispose() => Active.Value = _previous;
}
