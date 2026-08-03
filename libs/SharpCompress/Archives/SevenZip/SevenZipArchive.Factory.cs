using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.IO;
using SharpCompress.Readers;
using SharpCompress.Writers.SevenZip;

namespace SharpCompress.Archives.SevenZip;

public partial class SevenZipArchive
    : IWritableArchiveOpenable<SevenZipWriterOptions>,
        IMultiArchiveOpenable<
            IWritableArchive<SevenZipWriterOptions>,
            IWritableAsyncArchive<SevenZipWriterOptions>
        >
{
    public static ValueTask<IWritableAsyncArchive<SevenZipWriterOptions>> OpenAsyncArchive(
        string filePath,
        ReaderOptions? readerOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        filePath.NotNullOrEmpty(nameof(filePath));
        return new(
            (IWritableAsyncArchive<SevenZipWriterOptions>)
                OpenArchive(new FileInfo(filePath), readerOptions ?? ReaderOptions.ForFilePath)
        );
    }

    public static IWritableArchive<SevenZipWriterOptions> OpenArchive(
        string filePath,
        ReaderOptions? readerOptions = null
    )
    {
        filePath.NotNullOrEmpty(nameof(filePath));
        return OpenArchive(new FileInfo(filePath), readerOptions ?? ReaderOptions.ForFilePath);
    }

    public static IWritableArchive<SevenZipWriterOptions> OpenArchive(
        FileInfo fileInfo,
        ReaderOptions? readerOptions = null
    )
    {
        fileInfo.NotNull(nameof(fileInfo));
        return new SevenZipArchive(
            new SourceStream(
                fileInfo,
                i => ArchiveVolumeFactory.GetFilePart(i, fileInfo),
                readerOptions ?? ReaderOptions.ForFilePath
            )
        );
    }

    public static IWritableArchive<SevenZipWriterOptions> OpenArchive(
        IReadOnlyList<FileInfo> fileInfos,
        ReaderOptions? readerOptions = null
    )
    {
        fileInfos.NotNull(nameof(fileInfos));
        var files = fileInfos;
        return new SevenZipArchive(
            new SourceStream(
                files[0],
                i => i < files.Count ? files[i] : null,
                readerOptions ?? ReaderOptions.ForFilePath
            )
        );
    }

    public static IWritableArchive<SevenZipWriterOptions> OpenArchive(
        IReadOnlyList<Stream> streams,
        ReaderOptions? readerOptions = null
    )
    {
        var strms = streams.RequireReadable().RequireSeekable().ToList();
        return new SevenZipArchive(
            new SourceStream(
                strms[0],
                i => i < strms.Count ? strms[i] : null,
                readerOptions ?? ReaderOptions.ForExternalStream
            )
        );
    }

    public static IWritableArchive<SevenZipWriterOptions> OpenArchive(
        Stream stream,
        ReaderOptions? readerOptions = null
    )
    {
        stream.RequireReadable();
        stream.RequireSeekable();

        return new SevenZipArchive(
            new SourceStream(stream, _ => null, readerOptions ?? ReaderOptions.ForExternalStream)
        );
    }

    public static ValueTask<IWritableAsyncArchive<SevenZipWriterOptions>> OpenAsyncArchive(
        Stream stream,
        ReaderOptions? readerOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(
            (IWritableAsyncArchive<SevenZipWriterOptions>)OpenArchive(stream, readerOptions)
        );
    }

    public static ValueTask<IWritableAsyncArchive<SevenZipWriterOptions>> OpenAsyncArchive(
        FileInfo fileInfo,
        ReaderOptions? readerOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(
            (IWritableAsyncArchive<SevenZipWriterOptions>)OpenArchive(fileInfo, readerOptions)
        );
    }

    public static ValueTask<IWritableAsyncArchive<SevenZipWriterOptions>> OpenAsyncArchive(
        IReadOnlyList<Stream> streams,
        ReaderOptions? readerOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(
            (IWritableAsyncArchive<SevenZipWriterOptions>)OpenArchive(streams, readerOptions)
        );
    }

    public static ValueTask<IWritableAsyncArchive<SevenZipWriterOptions>> OpenAsyncArchive(
        IReadOnlyList<FileInfo> fileInfos,
        ReaderOptions? readerOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(
            (IWritableAsyncArchive<SevenZipWriterOptions>)OpenArchive(fileInfos, readerOptions)
        );
    }

    /// <summary>
    /// Creates a new, empty writable 7z archive ready to receive entries.
    /// </summary>
    public static IWritableArchive<SevenZipWriterOptions> CreateArchive() => new SevenZipArchive();

    /// <summary>
    /// Creates a new, empty writable async 7z archive ready to receive entries.
    /// </summary>
    public static ValueTask<IWritableAsyncArchive<SevenZipWriterOptions>> CreateAsyncArchive() =>
        new(new SevenZipArchive());

    public static bool IsSevenZipFile(string filePath) => IsSevenZipFile(new FileInfo(filePath));

    public static bool IsSevenZipFile(FileInfo fileInfo)
    {
        if (!fileInfo.Exists)
        {
            return false;
        }
        using Stream stream = fileInfo.OpenRead();
        return IsSevenZipFile(stream);
    }

    public static bool IsSevenZipFile(Stream stream) =>
        IsSevenZipFile(stream, ReaderOptions.ForExternalStream);

    public static bool IsSevenZipFile(Stream stream, ReaderOptions? readerOptions)
    {
        try
        {
            return SignatureMatch(stream, readerOptions?.LookForHeader ?? false);
        }
        catch
        {
            return false;
        }
    }

    public static async ValueTask<bool> IsSevenZipFileAsync(
        Stream stream,
        CancellationToken cancellationToken = default
    ) =>
        await IsSevenZipFileAsync(stream, ReaderOptions.ForExternalStream, cancellationToken)
            .ConfigureAwait(false);

    public static async ValueTask<bool> IsSevenZipFileAsync(
        Stream stream,
        ReaderOptions? readerOptions,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await SignatureMatchAsync(
                    stream,
                    readerOptions?.LookForHeader ?? false,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static ReadOnlySpan<byte> Signature => [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C];

    private static bool SignatureMatch(Stream stream, bool lookForHeader)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(6);
        try
        {
            var maxScanOffset = lookForHeader ? 0x80000 - 20 : 0;
            for (var offset = 0; offset <= maxScanOffset; offset++)
            {
                try
                {
                    stream.ReadExactly(buffer.AsSpan(0, 6));
                }
                catch (EndOfStreamException e)
                {
                    throw new IncompleteArchiveException("Unexpected end of stream.", e);
                }
                if (buffer.AsSpan().Slice(0, 6).SequenceEqual(Signature))
                {
                    return true;
                }

                if (!lookForHeader || !stream.CanSeek || stream.Length - stream.Position < 6)
                {
                    return false;
                }

                stream.Position -= 5;
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async ValueTask<bool> SignatureMatchAsync(
        Stream stream,
        bool lookForHeader,
        CancellationToken cancellationToken
    )
    {
        var buffer = ArrayPool<byte>.Shared.Rent(6);
        try
        {
            var maxScanOffset = lookForHeader ? 0x80000 - 20 : 0;
            for (var offset = 0; offset <= maxScanOffset; offset++)
            {
                if (
                    await stream
                        .ReadAtLeastAsync(
                            buffer.AsMemory(0, 6),
                            6,
                            throwOnEndOfStream: false,
                            cancellationToken
                        )
                        .ConfigureAwait(false) != 6
                )
                {
                    return false;
                }

                if (buffer.AsSpan().Slice(0, 6).SequenceEqual(Signature))
                {
                    return true;
                }

                if (!lookForHeader || !stream.CanSeek || stream.Length - stream.Position < 6)
                {
                    return false;
                }

                stream.Position -= 5;
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
