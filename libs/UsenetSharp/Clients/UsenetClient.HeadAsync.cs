using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public async Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
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
                static (self, id, timeout) => self.WriteMessageIdCommandAsync("HEAD", id, timeout))
                .ConfigureAwait(false);

            // Article retrieved - head follows (multi-line)
            if (responseCode == (int)UsenetResponseType.ArticleRetrievedHeadFollows)
            {
                UsenetArticleHeader headers;
                try
                {
                    headers = await ParseArticleHeadersAsync(operationCts.Token, allowDotTerminator: true)
                        .ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    RecordConnectionFailure(e);
                    throw;
                }

                return new UsenetHeadResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = responseCode,
                    ResponseMessage = response,
                    ArticleHeaders = headers
                };
            }

            await DrainUnexpectedMultiLineAsync(responseCode, operationCts.Token)
                .ConfigureAwait(false);

            return new UsenetHeadResponse
            {
                ResponseCode = responseCode,
                ResponseMessage = response,
                SegmentId = segmentId,
                ArticleHeaders = null
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }
}
