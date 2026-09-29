using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.DbBackupUpload;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(ConfigPathCollection))]
public sealed class DbBackupUploadTests : IDisposable
{
    private const string ValidDumpSql =
        "PRAGMA foreign_keys=OFF;\nBEGIN TRANSACTION;\nCREATE TABLE Marker(Value TEXT);\nCOMMIT;\n";

    private readonly string _root = Path.Join(Path.GetTempPath(), $"nzbdav-upload-{Guid.NewGuid():N}");
    private readonly string? _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
    private readonly DatabaseBackupStore _store = new();

    public DbBackupUploadTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _root);
        _store.EnsureInitialized();
    }

    [Fact]
    public async Task Upload_LegacyZipWithMetricsDump_KeepsOnlyMainAndWarden()
    {
        var result = await UploadZipAsync(
            ("db.sql", ValidDumpSql),
            ("warden.sql", ValidDumpSql),
            ("metrics.sql", "THIS IS NOT SQL"),
            ("manifest.json", """{"id":"legacy","files":[{"name":"metrics.sql","bytes":15}]}"""));

        Assert.IsType<OkObjectResult>(result);
        var backup = Assert.Single(_store.List());
        Assert.Equal(["db.sql", "warden.sql"], backup.Files.Select(file => file.Name));
        var directory = _store.GetBackupDirectory(backup.Id);
        Assert.False(File.Exists(Path.Join(directory, DatabaseBackupStore.MetricsSqlName)));
        Assert.True(File.Exists(Path.Join(directory, DatabaseBackupStore.ManifestFileName)));
    }

    [Fact]
    public async Task Upload_MetricsOnlyZip_IsRejected()
    {
        var result = await UploadZipAsync(("metrics.sql", ValidDumpSql));

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var body = Assert.IsType<BaseApiResponse>(bad.Value);
        Assert.Contains("db.sql", body.Error);
        Assert.Empty(_store.List());
        Assert.Empty(Directory.EnumerateDirectories(_store.BackupsRoot, ".tmp-*"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private sealed class TestUploadController(DatabaseBackupStore store)
        : DbBackupUploadController(store)
    {
        protected override bool RequiresAuthentication => false;
    }

    private async Task<IActionResult> UploadZipAsync(params (string Name, string Text)[] entries)
    {
        await using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                var entry = archive.CreateEntry(name);
                await using var entryStream = entry.Open();
                await using var writer = new StreamWriter(entryStream, Encoding.UTF8);
                await writer.WriteAsync(text);
            }
        }

        zip.Position = 0;
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "multipart/form-data; boundary=backup-test";
        context.Request.Form = new FormCollection(
            new Dictionary<string, StringValues>(),
            new FormFileCollection
            {
                new FormFile(zip, 0, zip.Length, "file", "legacy-backup.zip") { Headers = new HeaderDictionary() },
            });

        var controller = new TestUploadController(_store)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
        return await controller.HandleApiRequest();
    }
}