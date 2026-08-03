using System.IO.Pipelines;
using System.Text;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public Task<UsenetArticleResponse> ArticleAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return ArticleAsync(segmentId, null, cancellationToken);
    }

    public async Task<UsenetArticleResponse> ArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        ThrowIfDisposed();
        ValidateSegmentId(segmentId);
        try
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        var isReadBodyToPipeAsyncStarted = false;
        CancellationTokenSource? operationCts = null;

        try
        {
            ThrowIfDisposed();
            ThrowIfUnhealthy();
            ThrowIfNotConnected();
            operationCts = CreateOperationTokenSource(cancellationToken);
            using var ioTimeout = new CoalescedReadTimeout(
                operationCts.Token, _options.ReadTimeout, _timeProvider);

            var (responseCode, response) = await ExchangeSingleLineAsync(
                ioTimeout,
                segmentId,
                static (self, id, timeout) => self.WriteMessageIdCommandAsync("ARTICLE", id, timeout))
                .ConfigureAwait(false);

            // Article retrieved - head and body follow
            if (responseCode == (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            {
                UsenetArticleHeader headers;
                try
                {
                    headers = await ParseArticleHeadersAsync(operationCts.Token).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    RecordConnectionFailure(e);
                    throw;
                }

                // Create a pipe for streaming the body data
                var pipe = new Pipe(RawBodyPipeOptions);

                // Start background task to read the body and write to pipe
                isReadBodyToPipeAsyncStarted = true;
                _ = ReadBodyToPipeAsync(
                    pipe.Writer, operationCts, cancellationToken, onConnectionReadyAgain);
                operationCts = null;

                // Return immediately with the stream and headers
                return new UsenetArticleResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = responseCode,
                    ResponseMessage = response,
                    ArticleHeaders = headers,
                    Stream = pipe.Reader.AsStream(),
                };
            }

            await DrainUnexpectedMultiLineAsync(responseCode, operationCts.Token)
                .ConfigureAwait(false);

            return new UsenetArticleResponse()
            {
                ResponseCode = responseCode,
                ResponseMessage = response,
                SegmentId = segmentId,
                Stream = null,
                ArticleHeaders = null
            };
        }
        finally
        {
            if (!isReadBodyToPipeAsyncStarted)
            {
                operationCts?.Dispose();
                _commandLock.Release();
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            }
        }
    }

    private async Task<UsenetArticleHeader> ParseArticleHeadersAsync(
        CancellationToken cancellationToken,
        bool allowDotTerminator = false)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allHeaders = new List<KeyValuePair<string, string>>();
        string? currentHeaderName = null;
        var currentHeaderValue = new StringBuilder();
        var totalHeaderBytes = 0;
        var headerCount = 0;

        void CommitCurrentHeader()
        {
            if (currentHeaderName == null)
            {
                return;
            }

            var value = currentHeaderValue.ToString().Trim();
            headers.TryAdd(currentHeaderName, value);
            allHeaders.Add(new KeyValuePair<string, string>(currentHeaderName, value));
        }

        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line == null)
            {
                throw new UsenetProtocolException("Invalid NNTP response: missing article headers.");
            }

            // Empty line signals end of headers
            if (line == ".")
            {
                if (allowDotTerminator)
                {
                    CommitCurrentHeader();
                    break;
                }

                throw new UsenetProtocolException(
                    "Invalid NNTP response: article headers were not followed by a blank line.");
            }

            if (string.IsNullOrEmpty(line))
            {
                if (allowDotTerminator)
                {
                    // Malformed blank line inside a 221 block. Skip until the
                    // real terminator so the connection stays at a command boundary.
                    while (true)
                    {
                        var extra = await ReadLineAsync(cancellationToken).ConfigureAwait(false)
                            ?? throw new UsenetProtocolException(
                                "The NNTP connection closed before the header terminator was received.");

                        if (extra == ".")
                        {
                            break;
                        }

                        totalHeaderBytes += Encoding.Latin1.GetByteCount(extra) + 2;
                        if (totalHeaderBytes > 256 * 1024)
                        {
                            throw new UsenetProtocolException(
                                "NNTP article headers exceeded the 256 KiB limit.");
                        }
                    }

                    CommitCurrentHeader();
                    break;
                }

                // ARTICLE mode: blank line legitimately ends the header block.
                CommitCurrentHeader();
                break;
            }

            totalHeaderBytes += Encoding.Latin1.GetByteCount(line) + 2;
            if (totalHeaderBytes > 256 * 1024)
            {
                throw new UsenetProtocolException("NNTP article headers exceeded the 256 KiB limit.");
            }

            // Check if this is a continuation line (starts with whitespace)
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                // Append to current header value
                if (currentHeaderName != null)
                {
                    currentHeaderValue.Append(' ');
                    currentHeaderValue.Append(line.Trim());
                }
            }
            else
            {
                // Save the previous header if any
                CommitCurrentHeader();

                // Parse new header: "Name: Value"
                var colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    headerCount++;
                    if (headerCount > 256)
                    {
                        throw new UsenetProtocolException("NNTP article contained more than 256 headers.");
                    }

                    currentHeaderName = line.Substring(0, colonIndex).Trim();
                    currentHeaderValue.Clear();

                    // Get value after colon
                    if (colonIndex + 1 < line.Length)
                    {
                        currentHeaderValue.Append(line.Substring(colonIndex + 1).Trim());
                    }
                }
                else
                {
                    currentHeaderName = null;
                    currentHeaderValue.Clear();
                }
            }
        }

        return new UsenetArticleHeader
        {
            Headers = headers,
            AllHeaders = allHeaders
        };
    }
}
