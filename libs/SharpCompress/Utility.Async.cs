using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;

namespace SharpCompress;

internal static partial class Utility
{
    extension(Stream source)
    {
        public async ValueTask<long> TransferToAsync(
            Stream destination,
            long maxLength,
            int? bufferSize = null,
            CancellationToken cancellationToken = default
        )
        {
            // Use ReadOnlySubStream to limit reading and leverage framework's CopyToAsync
            using var limitedStream = new IO.ReadOnlySubStream(source, maxLength);
            await limitedStream
                .CopyToAsync(destination, bufferSize ?? Constants.BufferSize, cancellationToken)
                .ConfigureAwait(false);
            return limitedStream.Position;
        }
    }

    /// <summary>
    /// Opens a file stream for asynchronous writing.
    /// Uses File.OpenHandle with FileOptions.Asynchronous on .NET 8.0+ for optimal performance.
    /// Falls back to FileStream constructor with async options on legacy frameworks.
    /// </summary>
    /// <param name="path">The file path to open.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A FileStream configured for asynchronous operations.</returns>
    public static Stream OpenAsyncWriteStream(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Use File.OpenHandle with async options for .NET 8.0+
        var handle = File.OpenHandle(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            FileOptions.Asynchronous
        );
        return new FileStream(handle, FileAccess.Write);
    }

    /// <summary>
    /// Opens a file stream for asynchronous writing from a FileInfo.
    /// Uses File.OpenHandle with FileOptions.Asynchronous on .NET 8.0+ for optimal performance.
    /// Falls back to FileStream constructor with async options on legacy frameworks.
    /// </summary>
    /// <param name="fileInfo">The FileInfo to open.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A FileStream configured for asynchronous operations.</returns>
    public static Stream OpenAsyncWriteStream(
        this FileInfo fileInfo,
        CancellationToken cancellationToken
    )
    {
        fileInfo.NotNull(nameof(fileInfo));
        return OpenAsyncWriteStream(fileInfo.FullName, cancellationToken);
    }

    /// <summary>
    /// Opens a file stream for asynchronous reading.
    /// Uses File.OpenHandle with FileOptions.Asynchronous on .NET 8.0+ for optimal performance.
    /// Falls back to FileStream constructor with async options on legacy frameworks.
    /// </summary>
    /// <param name="path">The file path to open.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A FileStream configured for asynchronous operations.</returns>
    public static Stream OpenAsyncReadStream(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Use File.OpenHandle with async options for .NET 8.0+
        var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous
        );
        return new FileStream(handle, FileAccess.Read);
    }

    /// <summary>
    /// Opens a file stream for asynchronous reading from a FileInfo.
    /// Uses File.OpenHandle with FileOptions.Asynchronous on .NET 8.0+ for optimal performance.
    /// Falls back to FileStream constructor with async options on legacy frameworks.
    /// </summary>
    /// <param name="fileInfo">The FileInfo to open.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A FileStream configured for asynchronous operations.</returns>
    public static Stream OpenAsyncReadStream(
        this FileInfo fileInfo,
        CancellationToken cancellationToken
    )
    {
        fileInfo.NotNull(nameof(fileInfo));
        return OpenAsyncReadStream(fileInfo.FullName, cancellationToken);
    }
}
