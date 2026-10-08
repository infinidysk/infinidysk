using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;

namespace NzbWebDAV.Streams;

internal sealed class Par2CandidateReader(
    Par2FileProof proof,
    INntpClient client,
    Func<long, Memory<byte>, CancellationToken, Task> readCandidate,
    Func<bool>? geometryRecoverable = null,
    // Reads a candidate after a sequential failure; defaults to readCandidate.
    Func<long, Memory<byte>, CancellationToken, Task>? recoverCandidate = null)
{
    internal Task ReadAsync(long start, Memory<byte> target, CancellationToken cancellationToken) =>
        ReadVerifiedAsync(start, target, false, null, cancellationToken);

    internal Task ReadPrefixAsync(long start, Memory<byte> target, CancellationToken cancellationToken) =>
        ReadVerifiedAsync(start, target, true, null, cancellationToken);

    // The sequential read counts as the default attempt; a failure it would not have retried propagates.
    // A null failure is a checksum mismatch.
    internal Task RecoverAsync(long start, Memory<byte> target, Exception? failure, CancellationToken cancellationToken)
    {
        if (failure is not null && !IsRetryableCandidateFailure(failure, cancellationToken))
            ExceptionDispatchInfo.Throw(failure);
        return ReadVerifiedAsync(start, target, false, failure ?? ChecksumMismatch(), cancellationToken);
    }

    private static InvalidDataException ChecksumMismatch() => new("PAR2 candidate slice checksum mismatch.");

    private bool IsRetryableCandidateFailure(Exception exception, CancellationToken cancellationToken) =>
        !exception.IsCancellationException(cancellationToken)
        && !(exception is SeekPositionNotFoundException && geometryRecoverable?.Invoke() == true)
        && (exception is IOException or InvalidOperationException or NonRetryableDownloadException
            || exception.TryGetKnownErrorMessage(out _));

    private async Task ReadVerifiedAsync(
        long start, Memory<byte> target, bool prefix, Exception? sequentialFailure, CancellationToken cancellationToken)
    {
        if (!proof.IsValidFor(proof.FileLength))
            throw new InvalidDataException("Invalid persisted PAR2 verification metadata.");
        var padded = prefix || target.Length == proof.SliceSize ? target : new byte[proof.SliceSize];
        var sliceIndex = checked((int)(start / proof.SliceSize));
        var lastFailure = sequentialFailure;
        var read = sequentialFailure is null ? readCandidate : recoverCandidate ?? readCandidate;

        async Task<bool> TryCandidateAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            padded.Span.Clear();
            try
            {
                await read(start, padded[..target.Length], cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (prefix ? start == 0 && proof.VerifyPrefix(padded.Span) : proof.VerifySlice(padded.Span, sliceIndex))
                {
                    padded[..target.Length].CopyTo(target);
                    return true;
                }
                lastFailure = ChecksumMismatch();
            }
            // Retrying providers reuses the same recorded ranges; let the owner re-derive them.
            catch (Exception exception) when (IsRetryableCandidateFailure(exception, cancellationToken))
            {
                lastFailure = exception;
            }
            return false;
        }

        if (sequentialFailure is null && await TryCandidateAsync().ConfigureAwait(false)) return;
        if (WrappingNntpClient.Unwrap(client) is MultiProviderNntpClient providers)
        {
            foreach (var retry in providers.GetPar2VerificationProviders()
                         .Select(provider => new Par2VerificationReadContext(provider)))
            {
                using (retry)
                {
                    if (await TryCandidateAsync().ConfigureAwait(false)) return;
                }
            }
        }

        throw new NonRetryableDownloadException(
            "PAR2 slice integrity verification failed after bounded source attempts; no unverified bytes were returned. " +
            "Alternate message IDs are tried for article failures, but checksum-failing combinations are not exhaustively searched.",
            lastFailure);
    }
}
