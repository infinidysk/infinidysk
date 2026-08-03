using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.Tar.Headers;
using SharpCompress.IO;
using SharpCompress.Readers;
using SharpCompress.Writers.Tar;

namespace SharpCompress.Archives.Tar;

public partial class TarArchive
    : IWritableArchiveOpenable<TarWriterOptions>,
        IMultiArchiveOpenable<
            IWritableArchive<TarWriterOptions>,
            IWritableAsyncArchive<TarWriterOptions>
        >
{
    public static IWritableArchive<TarWriterOptions> OpenArchive(
        string filePath,
        ReaderOptions? readerOptions = null
    )
    {
        filePath.NotNullOrEmpty(nameof(filePath));
        return OpenArchive(new FileInfo(filePath), readerOptions);
    }

    public static IWritableArchive<TarWriterOptions> OpenArchive(
        FileInfo fileInfo,
        ReaderOptions? readerOptions = null
    )
    {
        fileInfo.NotNull(nameof(fileInfo));
        return OpenArchive([fileInfo], readerOptions ?? ReaderOptions.ForFilePath);
    }

    public static IWritableArchive<TarWriterOptions> OpenArchive(
        IReadOnlyList<FileInfo> fileInfos,
        ReaderOptions? readerOptions = null
    )
    {
        fileInfos.NotNull(nameof(fileInfos));
        var files = fileInfos;
        var sourceStream = new SourceStream(
            files[0],
            i => i < files.Count ? files[i] : null,
            readerOptions ?? ReaderOptions.ForFilePath
        );
        return OpenValidatedArchive(sourceStream);
    }

    public static IWritableArchive<TarWriterOptions> OpenArchive(
        IReadOnlyList<Stream> streams,
        ReaderOptions? readerOptions = null
    )
    {
        var strms = streams.RequireReadable().RequireSeekable().ToList();
        var sourceStream = new SourceStream(
            strms[0],
            i => i < strms.Count ? strms[i] : null,
            readerOptions ?? ReaderOptions.ForExternalStream
        );
        return OpenValidatedArchive(sourceStream);
    }

    public static IWritableArchive<TarWriterOptions> OpenArchive(
        Stream stream,
        ReaderOptions? readerOptions = null
    )
    {
        stream.RequireReadable();
        stream.RequireSeekable();

        return OpenArchive([stream], readerOptions);
    }

    public static async ValueTask<IWritableAsyncArchive<TarWriterOptions>> OpenAsyncArchive(
        Stream stream,
        ReaderOptions? readerOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        stream.RequireReadable();
        stream.RequireSeekable();
        var sourceStream = new SourceStream(
            stream,
            i => null,
            readerOptions ?? ReaderOptions.ForExternalStream
        );
        return await OpenValidatedArchiveAsync(sourceStream, cancellationToken)
            .ConfigureAwait(false);
    }

    public static ValueTask<IWritableAsyncArchive<TarWriterOptions>> OpenAsyncArchive(
        string filePath,
        ReaderOptions? readerOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        filePath.NotNullOrEmpty(nameof(filePath));
        return OpenAsyncArchive(new FileInfo(filePath), readerOptions, cancellationToken);
    }

    public static async ValueTask<IWritableAsyncArchive<TarWriterOptions>> OpenAsyncArchive(
        FileInfo fileInfo,
        ReaderOptions? readerOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        fileInfo.NotNull(nameof(fileInfo));
        readerOptions ??= ReaderOptions.ForFilePath;
        var sourceStream = new SourceStream(fileInfo, i => null, readerOptions);
        return await OpenValidatedArchiveAsync(sourceStream, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async ValueTask<IWritableAsyncArchive<TarWriterOptions>> OpenAsyncArchive(
        IReadOnlyList<Stream> streams,
        ReaderOptions? readerOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var strms = streams.RequireReadable().RequireSeekable().ToList();
        var sourceStream = new SourceStream(
            strms[0],
            i => i < strms.Count ? strms[i] : null,
            readerOptions ?? ReaderOptions.ForExternalStream
        );
        return await OpenValidatedArchiveAsync(sourceStream, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async ValueTask<IWritableAsyncArchive<TarWriterOptions>> OpenAsyncArchive(
        IReadOnlyList<FileInfo> fileInfos,
        ReaderOptions? readerOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        fileInfos.NotNull(nameof(fileInfos));
        var files = fileInfos;
        var sourceStream = new SourceStream(
            files[0],
            i => i < files.Count ? files[i] : null,
            readerOptions ?? ReaderOptions.ForFilePath
        );
        return await OpenValidatedArchiveAsync(sourceStream, cancellationToken)
            .ConfigureAwait(false);
    }

    public static bool IsTarFile(string filePath) => IsTarFile(new FileInfo(filePath));

    public static bool IsTarFile(FileInfo fileInfo)
    {
        if (!fileInfo.Exists)
        {
            return false;
        }
        using Stream stream = fileInfo.OpenRead();
        return IsTarFile(stream);
    }

    public static bool IsTarFile(Stream stream)
    {
        try
        {
            var tarHeader = new TarHeader(new ArchiveEncoding());
            var reader = new BinaryReader(stream, Encoding.UTF8, false);
            var readSucceeded = tarHeader.Read(reader);
            var isEmptyArchive =
                tarHeader.Name?.Length == 0
                && tarHeader.Size == 0
                && IsDefined(tarHeader.EntryType);
            return readSucceeded || isEmptyArchive;
        }
        catch (Exception)
        {
            // Catch all exceptions during tar header reading to determine if this is a valid tar file
            // Invalid tar files or corrupted streams will throw various exceptions
            return false;
        }
    }

    public static async ValueTask<bool> IsTarFileAsync(
        Stream stream,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var tarHeader = new TarHeader(new ArchiveEncoding());
            await using var reader = new AsyncBinaryReader(stream, leaveOpen: true);
            var readSucceeded = await tarHeader
                .ReadAsync(reader, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var isEmptyArchive =
                tarHeader.Name?.Length == 0
                && tarHeader.Size == 0
                && IsDefined(tarHeader.EntryType);
            return readSucceeded || isEmptyArchive;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Catch all exceptions during tar header reading to determine if this is a valid tar file
            // Invalid tar files or corrupted streams will throw various exceptions
            return false;
        }
    }

    public static IWritableArchive<TarWriterOptions> CreateArchive() => new TarArchive();

    public static ValueTask<IWritableAsyncArchive<TarWriterOptions>> CreateAsyncArchive() =>
        new(new TarArchive());

    private static TarArchive OpenValidatedArchive(SourceStream sourceStream)
    {
        try
        {
            EnsureRawTarFile(sourceStream);
            return new TarArchive(sourceStream);
        }
        catch
        {
            sourceStream.Dispose();
            throw;
        }
    }

    private static async ValueTask<TarArchive> OpenValidatedArchiveAsync(
        SourceStream sourceStream,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await EnsureRawTarFileAsync(sourceStream, cancellationToken).ConfigureAwait(false);
            return new TarArchive(sourceStream);
        }
        catch
        {
            await sourceStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void EnsureRawTarFile(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        if (!IsTarFile(stream))
        {
            stream.Seek(0, SeekOrigin.Begin);
            throw new InvalidFormatException("Not a tar file.");
        }
        stream.Seek(0, SeekOrigin.Begin);
    }

    private static async ValueTask EnsureRawTarFileAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        stream.Seek(0, SeekOrigin.Begin);
        if (!await IsTarFileAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            stream.Seek(0, SeekOrigin.Begin);
            throw new InvalidFormatException("Not a tar file.");
        }
        stream.Seek(0, SeekOrigin.Begin);
    }

    private static bool IsDefined(EntryType value)
    {
        return Enum.IsDefined(value);
    }
}
