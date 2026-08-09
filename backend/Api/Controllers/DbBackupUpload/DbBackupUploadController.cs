using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Backup;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.DbBackupUpload;

[ApiController]
[Route("api/db-backup-upload")]
[RequestSizeLimit(4L * 1024 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 4L * 1024 * 1024 * 1024)]
public class DbBackupUploadController(DatabaseBackupStore store) : BaseApiController
{
    private const long MaxRestoreBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxRecognizedZipEntries = 4;
    private const int HeaderSampleCharacters = 4 * 1024;
    private const long MaxCompressionRatio = 200;

    protected override async Task<IActionResult> HandleRequest()
    {
        // Allow large backup uploads regardless of the global Kestrel body limit.
        var sizeFeature = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = null;

        if (!HttpContext.Request.HasFormContentType)
            throw new BadHttpRequestException("Expected multipart form body.");

        var formFiles = HttpContext.Request.Form.Files;
        var file = formFiles.Count > 0 ? formFiles[0] : null;
        if (file is null || file.Length == 0)
            throw new BadHttpRequestException("No backup file was uploaded.");

        store.EnsureInitialized();
        var stagingPath = store.CreateStaging(DatabaseBackupKinds.Uploaded);
        try
        {
            var fileName = file.FileName ?? "upload.zip";
            if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractZipAsync(file, stagingPath, HttpContext.RequestAborted).ConfigureAwait(false);
            }
            else if (fileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                if (file.Length > MaxRestoreBytes)
                    throw PayloadTooLarge("SQL dump exceeds the restore size limit.");

                var target = Path.Join(stagingPath, DatabaseBackupStore.DbSqlName);
                await using var stream = file.OpenReadStream();
                await using var output = System.IO.File.Create(target);
                await CopyWithLimitAsync(stream, output, MaxRestoreBytes, HttpContext.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                throw new BadHttpRequestException("Upload must be a .zip backup or a .sql dump.");
            }

            ValidateSqlFiles(stagingPath);

            var notes = HttpContext.Request.Form["notes"].ToString();
            var manifest = store.CommitStaging(
                stagingPath,
                DatabaseBackupKinds.Uploaded,
                string.IsNullOrWhiteSpace(notes) ? $"Uploaded from {Path.GetFileName(fileName)}" : notes,
                preserved: false,
                appVersion: ConfigManager.AppVersion,
                lastMainMigration: null);
            stagingPath = null;

            return Ok(new { status = true, backup = manifest });
        }
        finally
        {
            store.DiscardStaging(stagingPath);
        }
    }

    private static async Task ExtractZipAsync(IFormFile file, string stagingPath, CancellationToken ct)
    {
        await using var upload = file.OpenReadStream();
        using var archive = new ZipArchive(upload, ZipArchiveMode.Read, leaveOpen: false);
        var extractedBytes = 0L;
        var recognizedEntries = 0;
        var extractedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var relative = entry.FullName.Replace('\\', '/');
            if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new BadHttpRequestException("Zip entry path is invalid.");

            var fileName = Path.GetFileName(relative);
            if (fileName is not (DatabaseBackupStore.DbSqlName
                or DatabaseBackupStore.MetricsSqlName
                or DatabaseBackupStore.WardenSqlName
                or DatabaseBackupStore.ManifestFileName))
                continue;

            if (!extractedNames.Add(fileName))
                throw new BadHttpRequestException($"Zip contains duplicate {fileName} entries.");

            if (++recognizedEntries > MaxRecognizedZipEntries)
                throw PayloadTooLarge("Zip contains too many recognized backup entries.");

            if (entry.Length > MaxRestoreBytes || extractedBytes > MaxRestoreBytes - entry.Length)
                throw PayloadTooLarge("Zip extraction exceeds the restore size limit.");

            if (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxCompressionRatio)
                throw PayloadTooLarge("Zip entry compression ratio exceeds the restore safety limit.");

            var dest = Path.Join(stagingPath, fileName);
            await using var entryStream = await entry.OpenAsync(ct).ConfigureAwait(false);
            await using var output = System.IO.File.Create(dest);
            extractedBytes += await CopyWithLimitAsync(
                entryStream,
                output,
                MaxRestoreBytes - extractedBytes,
                ct).ConfigureAwait(false);
        }
    }

    private static async Task<long> CopyWithLimitAsync(Stream input, Stream output, long remainingBytes, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var copied = 0L;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (copied > remainingBytes - read)
                throw PayloadTooLarge("Backup data exceeds the restore size limit.");

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            copied += read;
        }

        return copied;
    }

    private static void ValidateSqlFiles(string stagingPath)
    {
        var sqlFiles = new[]
        {
            DatabaseBackupStore.DbSqlName,
            DatabaseBackupStore.MetricsSqlName,
            DatabaseBackupStore.WardenSqlName,
        };

        var found = false;
        foreach (var name in sqlFiles)
        {
            var path = Path.Join(stagingPath, name);
            if (!System.IO.File.Exists(path))
                continue;
            found = true;
            using var reader = new StreamReader(path);
            var sampleBuffer = new char[HeaderSampleCharacters];
            var sampleLength = reader.ReadBlock(sampleBuffer, 0, sampleBuffer.Length);
            var sample = new string(sampleBuffer, 0, sampleLength);
            if (!sample.Contains("PRAGMA foreign_keys", StringComparison.OrdinalIgnoreCase)
                && !sample.Contains("BEGIN", StringComparison.OrdinalIgnoreCase)
                && !sample.Contains("CREATE", StringComparison.OrdinalIgnoreCase)
                && !sample.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
            {
                throw new BadHttpRequestException($"{name} does not look like a SQLite SQL dump.");
            }
        }

        if (!found)
            throw new BadHttpRequestException("Upload did not contain any recognized .sql dump files.");
    }

    private static BadHttpRequestException PayloadTooLarge(string message) =>
        new(message, StatusCodes.Status413PayloadTooLarge);
}
