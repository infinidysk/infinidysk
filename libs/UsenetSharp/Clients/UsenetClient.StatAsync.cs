using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public async Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateSegmentId(segmentId);
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfUnhealthy();
            ThrowIfNotConnected();
            using var operationCts = CreateOperationTokenSource(cancellationToken);
            using var ioTimeout = new CoalescedReadTimeout(
                operationCts.Token, _options.ReadTimeout, _timeProvider);

            var (responseCode, response) = await ExchangeSingleLineAsync(
                ioTimeout,
                segmentId,
                static (self, id, timeout) => self.WriteMessageIdCommandAsync("STAT", id, timeout))
                .ConfigureAwait(false);

            if (responseCode != (int)UsenetResponseType.ArticleExists)
            {
                await DrainUnexpectedMultiLineAsync(responseCode, operationCts.Token)
                    .ConfigureAwait(false);
            }

            return new UsenetStatResponse()
            {
                ResponseCode = responseCode,
                ResponseMessage = response,
                ArticleExists = responseCode == (int)UsenetResponseType.ArticleExists,
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }
}
