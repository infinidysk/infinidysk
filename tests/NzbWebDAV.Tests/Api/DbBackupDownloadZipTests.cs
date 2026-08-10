using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;
using NzbWebDAV.Api.Controllers.DbBackupDownload;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Api;

public class DbBackupDownloadZipTests
{
    [Fact]
    public async Task WriteBackupZipAsync_ThroughBodyWriterStyleStream_ProducesReadableZipExcludingRollback()
    {
        var backupDir = CreateStagingBackup();
        try
        {
            // Mirror the controller: ZipArchive sync-writes into BodyWriter.AsStream(),
            // which tolerates sync I/O (unlike Kestrel's Response.Body).
            var pipe = new Pipe(new PipeOptions(
                pauseWriterThreshold: long.MaxValue,
                resumeWriterThreshold: long.MaxValue / 2));

            await using (var writerStream = pipe.Writer.AsStream(leaveOpen: true))
            {
                await DbBackupDownloadController.WriteBackupZipAsync(backupDir, writerStream);
            }

            await pipe.Writer.CompleteAsync();

            await using var zipStream = new MemoryStream();
            await pipe.Reader.CopyToAsync(zipStream);
            await pipe.Reader.CompleteAsync();
            zipStream.Position = 0;

            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            var names = archive.Entries.Select(e => e.FullName).OrderBy(x => x, StringComparer.Ordinal).ToList();

            Assert.Equal(
                ["db.sql", "manifest.json"],
                names);
            Assert.DoesNotContain(names, n => n.StartsWith("rollback/", StringComparison.OrdinalIgnoreCase));

            var dbEntry = archive.GetEntry("db.sql");
            Assert.NotNull(dbEntry);
            await using var entryStream = dbEntry!.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8);
            Assert.Equal("SELECT 1;", await reader.ReadToEndAsync());
        }
        finally
        {
            Directory.Delete(backupDir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteBackupZipAsync_AgainstAsyncOnlyStream_ThrowsLikeKestrelResponseBody()
    {
        // Documents why the controller must use BodyWriter.AsStream() instead of Response.Body:
        // ZipArchive still performs synchronous writes on .NET 10.
        var backupDir = CreateStagingBackup();
        try
        {
            await using var inner = new MemoryStream();
            await using var asyncOnly = new AsyncOnlyStream(inner);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => DbBackupDownloadController.WriteBackupZipAsync(backupDir, asyncOnly));

            Assert.Contains("Synchronous operations are disallowed", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(backupDir, recursive: true);
        }
    }

    private static string CreateStagingBackup()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-backup-zip-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Join(dir, DatabaseBackupStore.RollbackFolderName));

        File.WriteAllText(Path.Join(dir, "db.sql"), "SELECT 1;");
        File.WriteAllText(Path.Join(dir, "manifest.json"), """{"id":"test"}""");
        File.WriteAllText(
            Path.Join(dir, DatabaseBackupStore.RollbackFolderName, "db.sql"),
            "SHOULD_NOT_BE_IN_ZIP");

        return dir;
    }

    /// <summary>
    /// Mimics Kestrel's Response.Body: async I/O only; sync Write/Flush throw.
    /// </summary>
    private sealed class AsyncOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() =>
            throw new InvalidOperationException(
                "Synchronous operations are disallowed. Call WriteAsync or set AllowSynchronousIO to true instead.");

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException(
                "Synchronous operations are disallowed. Call WriteAsync or set AllowSynchronousIO to true instead.");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException(
                "Synchronous operations are disallowed. Call WriteAsync or set AllowSynchronousIO to true instead.");

        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new InvalidOperationException(
                "Synchronous operations are disallowed. Call WriteAsync or set AllowSynchronousIO to true instead.");

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
