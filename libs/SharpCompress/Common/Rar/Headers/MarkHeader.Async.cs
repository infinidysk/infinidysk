using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common.Rar;

namespace SharpCompress.Common.Rar.Headers;

internal partial class MarkHeader
{
    private static async ValueTask<byte> GetByteAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken
    )
    {
        var bytesRead = await stream
            .ReadAsync(buffer.AsMemory(0, 1), cancellationToken)
            .ConfigureAwait(false);
        if (bytesRead == 1)
        {
            return buffer[0];
        }
        throw new RarHeaderReadException(
            "Unexpected end of stream while reading RAR signature.",
            truncated: true
        );
    }

    public static async ValueTask<MarkHeader> ReadAsync(
        Stream stream,
        bool leaveStreamOpen,
        bool lookForHeader,
        CancellationToken cancellationToken = default
    )
    {
        var maxScanIndex = lookForHeader ? MAX_SFX_SIZE : 0;
        var buffer = new byte[1];
        try
        {
            var start = -1;
            var b = await GetByteAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            start++;
            while (start <= maxScanIndex)
            {
                if (b == 0x52)
                {
                    b = await GetByteAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
                    start++;
                    if (b == 0x61)
                    {
                        b = await GetByteAsync(stream, buffer, cancellationToken)
                            .ConfigureAwait(false);
                        start++;
                        if (b != 0x72)
                        {
                            continue;
                        }

                        b = await GetByteAsync(stream, buffer, cancellationToken)
                            .ConfigureAwait(false);
                        start++;
                        if (b != 0x21)
                        {
                            continue;
                        }

                        b = await GetByteAsync(stream, buffer, cancellationToken)
                            .ConfigureAwait(false);
                        start++;
                        if (b != 0x1a)
                        {
                            continue;
                        }

                        b = await GetByteAsync(stream, buffer, cancellationToken)
                            .ConfigureAwait(false);
                        start++;
                        if (b != 0x07)
                        {
                            continue;
                        }

                        b = await GetByteAsync(stream, buffer, cancellationToken)
                            .ConfigureAwait(false);
                        start++;
                        if (b == 1)
                        {
                            b = await GetByteAsync(stream, buffer, cancellationToken)
                                .ConfigureAwait(false);
                            start++;
                            if (b != 0)
                            {
                                continue;
                            }

                            return new MarkHeader(true); // Rar5
                        }
                        else if (b == 0)
                        {
                            return new MarkHeader(false); // Rar4
                        }
                    }
                    else if (b == 0x45)
                    {
                        b = await GetByteAsync(stream, buffer, cancellationToken)
                            .ConfigureAwait(false);
                        start++;
                        if (b != 0x7e)
                        {
                            continue;
                        }

                        b = await GetByteAsync(stream, buffer, cancellationToken)
                            .ConfigureAwait(false);
                        start++;
                        if (b != 0x5e)
                        {
                            continue;
                        }

                        throw new RarHeaderReadException(
                            "Rar format version pre-4 is unsupported.",
                            truncated: false
                        );
                    }
                }
                else
                {
                    b = await GetByteAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
                    start++;
                }
            }
        }
        catch (RarHeaderReadException)
        {
            if (!leaveStreamOpen)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception e)
        {
            if (!leaveStreamOpen)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            throw new RarHeaderReadException(
                "Error trying to read rar signature.",
                truncated: false,
                e
            );
        }

        throw new RarHeaderReadException("Rar signature not found", truncated: false);
    }
}
