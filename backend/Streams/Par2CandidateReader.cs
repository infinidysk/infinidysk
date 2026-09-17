using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;

namespace NzbWebDAV.Streams;

internal sealed class Par2CandidateReader(
    Par2FileProof proof,
    INntpClient client,
    Func<long, Memory<byte>, CancellationToken, Task> readCandidate)
{
    internal Task ReadAsync(long start, Memory<byte> target, CancellationToken cancellationToken) =>
        ReadVerifiedAsync(start, target, false, cancellationToken);

    internal Task ReadPrefixAsync(long start, Memory<byte> target, CancellationToken cancellationToken) =>
        ReadVerifiedAsync(start, target, true, cancellationToken);

    private async Task ReadVerifiedAsync(long start, Memory<byte> target, bool prefix, CancellationToken cancellationToken)
    {
        if (!proof.IsValidFor(proof.FileLength))
            throw new InvalidDataException("Invalid persisted PAR2 verification metadata.");
        var padded = prefix || target.Length == proof.SliceSize ? target : new byte[proof.SliceSize];
        var sliceIndex = checked((int)(start / proof.SliceSize));
        Exception? lastFailure = null;

        async Task<bool> TryCandidateAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            padded.Span.Clear();
            try
            {
                await readCandidate(start, padded[..target.Length], cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (prefix ? start == 0 && proof.VerifyPrefix(padded.Span) : proof.VerifySlice(padded.Span, sliceIndex))
                {
                    padded[..target.Length].CopyTo(target);
                    return true;
                }
                lastFailure = new InvalidDataException("PAR2 candidate slice checksum mismatch.");
            }
            catch (Exception exception) when (!exception.IsCancellationException(cancellationToken)
                && (exception is IOException or InvalidOperationException or NonRetryableDownloadException
                    || exception.TryGetKnownErrorMessage(out _)))
            {
                lastFailure = exception;
            }
            return false;
        }

        if (await TryCandidateAsync().ConfigureAwait(false)) return;
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