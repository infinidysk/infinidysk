using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;

namespace NzbWebDAV.Tests.Services;

/// <summary>
/// A DavItem whose streaming payload is gone (database-only restore, blob
/// loss) must surface as a missing-payload failure rather than being treated
/// as a zero-segment healthy file.
/// </summary>
public sealed class HealthCheckMissingPayloadTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-health-payload-{Guid.NewGuid():N}.sqlite");
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
        _dbClient = new DavDatabaseClient(_context);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        try { File.Delete(_databasePath); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task NzbFile_WithoutPayloadOrLegacyRow_LooksUpNull()
    {
        var item = NewUsenetFile(DavItem.ItemSubType.NzbFile, fileBlobId: null);
        _context.Items.Add(item);
        await _context.SaveChangesAsync();

        Assert.Null(await _dbClient.GetDavNzbFileAsync(item));
    }

    [Fact]
    public async Task MultipartFile_WithDanglingFileBlobId_LooksUpNull()
    {
        var item = NewUsenetFile(DavItem.ItemSubType.MultipartFile, fileBlobId: Guid.NewGuid());
        _context.Items.Add(item);
        await _context.SaveChangesAsync();

        // Dangling blob reference: no blob file and no legacy row.
        Assert.Null(await _dbClient.GetDavMultipartFileAsync(item));
    }

    [Fact]
    public async Task NzbFile_WithLegacyRow_ResolvesItsPayload()
    {
        var item = NewUsenetFile(DavItem.ItemSubType.NzbFile, fileBlobId: null);
        _context.Items.Add(item);
        _context.NzbFiles.Add(new DavNzbFile { Id = item.Id, SegmentIds = [$"<{Guid.NewGuid():N}@test>"] });
        await _context.SaveChangesAsync();

        var file = await _dbClient.GetDavNzbFileAsync(item);
        Assert.NotNull(file);
        Assert.Single(file.SegmentIds);
    }

    [Fact]
    public void MissingFilePayloadException_CarriesIdentityContext()
    {
        var payloadId = Guid.NewGuid();
        var item = NewUsenetFile(DavItem.ItemSubType.MultipartFile, fileBlobId: payloadId);

        var ex = new MissingFilePayloadException(item, DavItem.ItemSubType.MultipartFile);

        Assert.Equal(item.Id, ex.DavItemId);
        Assert.Equal(payloadId, ex.FileBlobId);
        Assert.Equal(item.Path, ex.FilePath);
        Assert.Equal(DavItem.ItemSubType.MultipartFile, ex.StoreKind);
        Assert.Contains(payloadId.ToString(), ex.Message, StringComparison.Ordinal);
    }

    private static DavItem NewUsenetFile(DavItem.ItemSubType subType, Guid? fileBlobId)
    {
        return DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            $"payload-{Guid.NewGuid():N}.mkv",
            fileSize: 100,
            DavItem.ItemType.UsenetFile,
            subType,
            releaseDate: DateTimeOffset.UtcNow.AddDays(-1),
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: fileBlobId);
    }
}
