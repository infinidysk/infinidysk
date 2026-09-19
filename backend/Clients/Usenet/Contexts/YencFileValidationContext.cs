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
    private readonly bool _deferToPar2Proof;

    private YencFileValidationContext(
        int expectedTotalParts,
        string stage = "Unknown",
        NzbFile? file = null,
        string[]? segmentIds = null,
        string[][]? segmentFallbacks = null,
        bool deferToPar2Proof = false)
    {
        ExpectedTotalParts = expectedTotalParts;
        Stage = stage;
        _file = file;
        _segmentIds = segmentIds;
        _segmentFallbacks = segmentFallbacks;
        _deferToPar2Proof = deferToPar2Proof;
        Active.Value = this;
    }

    public static YencFileValidationContext? Current => Active.Value;
    public static int? CurrentExpectedTotalParts => Current?.ExpectedTotalParts;
    public int ExpectedTotalParts { get; }
    public string Stage { get; }

    public static bool MatchesExpectedFile(UsenetYencHeader header) =>
        Current?._deferToPar2Proof == true
        || CurrentExpectedTotalParts is not { } expectedTotalParts
        || (expectedTotalParts == 1 && header.TotalParts == 0)
        || (header.HasTotalParts == false && header.TotalParts == 0)
        || header.TotalParts == expectedTotalParts;

    public static bool MatchesExpectedFile(UsenetYencHeader header, string requestedId) =>
        MatchesExpectedFile(header) || (Current?.MatchesRequestedPosition(header, requestedId) ?? false);

    // Some obfuscated uploads put random numbers in the yEnc total= field, so the total alone
    // can't tell a decoy from a wrong article. The part number and byte offset can: a wrong
    // article points at a different position, a decoy points at the one we asked for.
    private bool MatchesRequestedPosition(UsenetYencHeader header, string requestedId)
    {
        var (_, position, _) = GetRequestDetails(requestedId);
        if (position is not { } requestedPosition || header.PartNumber != requestedPosition)
            return false;

        if (requestedPosition == ExpectedTotalParts)
            return true;

        return header.PartSize > 0 && header.PartOffset == (long)(requestedPosition - 1) * header.PartSize;
    }

    public static IDisposable Begin(int expectedTotalParts) =>
        new YencFileValidationContext(expectedTotalParts);

    public static IDisposable BeginSizeProbe(NzbFile file) =>
        new YencFileValidationContext(file.Segments.Count, "SizeProbe", file: file);

    public static IDisposable BeginStreaming(string[] segmentIds, string[][]? segmentFallbacks) =>
        new YencFileValidationContext(
            segmentIds.Length, "Streaming", segmentIds: segmentIds, segmentFallbacks: segmentFallbacks,
            deferToPar2Proof: Current?._deferToPar2Proof == true
                && ReferenceEquals(Current._segmentIds, segmentIds));

    internal static IDisposable BeginBufferedPar2ProofRead(string[] segmentIds, string[][]? segmentFallbacks) =>
        new YencFileValidationContext(
            segmentIds.Length, "BufferedPar2ProofRead", segmentIds: segmentIds,
            segmentFallbacks: segmentFallbacks, deferToPar2Proof: true);

    public (string? FileAnchor, int? Position, int? NzbNumber) GetRequestDetails(string requestedId)
    {
        if (_file is { Segments.Count: > 0 })
        {
            for (var index = 0; index < _file.Segments.Count; index++)
            {
                var segment = _file.Segments[index];
                if (string.Equals(segment.MessageId, requestedId, StringComparison.Ordinal)
                    || Array.IndexOf(segment.FallbackMessageIds, requestedId) >= 0)
                    return (_file.Segments[0].MessageId, index + 1, segment.Number);
            }

            return (_file.Segments[0].MessageId, null, null);
        }

        if (_segmentIds is { Length: > 0 })
        {
            var index = Array.IndexOf(_segmentIds, requestedId);
            if (index >= 0) return (_segmentIds[0], index + 1, null);
            if (_segmentFallbacks is not null)
            {
                for (index = 0; index < Math.Min(_segmentIds.Length, _segmentFallbacks.Length); index++)
                {
                    if (_segmentFallbacks[index] is { } fallbacks && Array.IndexOf(fallbacks, requestedId) >= 0)
                        return (_segmentIds[0], index + 1, null);
                }
            }

            return (_segmentIds[0], null, null);
        }

        return (null, null, null);
    }

    public void ReportMismatch(string requestedId, string providerKey, int responseCode, UsenetYencHeader header)
    {
        var (fileAnchor, position, nzbNumber) = GetRequestDetails(requestedId);
        var fileRef = Reference(fileAnchor);
        var articleRef = Reference(requestedId);
        var providerRef = Reference(providerKey);
        var returnedNameRef = Reference(header.FileName);
        var key = $"yenc-mismatch/{Stage}/{fileRef}/{articleRef}/{providerRef}/{position}/{nzbNumber}/" +
                  $"{ExpectedTotalParts}/{responseCode}/{returnedNameRef}/{header.PartNumber}/{header.TotalParts}/" +
                  $"{header.FileSize}/{header.PartOffset}/{header.PartSize}/{header.LineLength}";
        ThrottledSegmentWarning.Write(
            key,
            "Rejected yEnc article because its parsed total differs from the active file segment count. " +
            "Stage: {Stage}; FileRef: {FileRef}; ArticleRef: {ArticleRef}; ProviderRef: {ProviderRef}; " +
            "RequestedSegmentPosition: {RequestedSegmentPosition}; NzbSegmentNumber: {NzbSegmentNumber}; " +
            "ExpectedTotalParts: {ExpectedTotalParts}; ResponseCode: {ResponseCode}; " +
            "ReturnedNameRef: {ReturnedNameRef}; ReturnedPartNumber: {ReturnedPartNumber}; " +
            "ReturnedTotalParts: {ReturnedTotalParts}; ReturnedFileSize: {ReturnedFileSize}; " +
            "ReturnedPartOffset: {ReturnedPartOffset}; ReturnedPartSize: {ReturnedPartSize}; " +
            "ReturnedLineLength: {ReturnedLineLength}; MetadataSource: ParsedYencHeader",
            Stage, fileRef, articleRef, providerRef, position, nzbNumber, ExpectedTotalParts, responseCode,
            returnedNameRef, header.PartNumber, header.TotalParts, header.FileSize, header.PartOffset,
            header.PartSize, header.LineLength);
    }

    private static string? Reference(string? value) => value is null
        ? null
        : Convert.ToHexString(HMACSHA256.HashData(DiagnosticKey, Encoding.UTF8.GetBytes(value)));

    public void Dispose() => Active.Value = _previous;
}