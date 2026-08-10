using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public sealed class MultipartFileSizeRepairServiceTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-filesize-repair-cfg-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DavDatabaseContext _context = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task RepairAsync_FixesFullyResolvedUnencryptedMaxValueRow()
    {
        var blobId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await SeedMultipartAsync(
            itemId,
            blobId,
            fileSize: long.MaxValue,
            isLazy: false,
            aes: null,
            packedParts: [40, 60]);

        var (repaired, skippedLazy, skippedUnsafe, missing) =
            await MultipartFileSizeRepairService.RepairAsync(_context, CancellationToken.None);

        Assert.Equal(1, repaired);
        Assert.Equal(0, skippedLazy);
        Assert.Equal(0, skippedUnsafe);
        Assert.Equal(0, missing);

        _context.ChangeTracker.Clear();
        var item = await _context.Items.AsNoTracking().SingleAsync(x => x.Id == itemId);
        Assert.Equal(100, item.FileSize);
    }

    [Fact]
    public async Task RepairAsync_IsIdempotent()
    {
        var blobId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await SeedMultipartAsync(
            itemId,
            blobId,
            fileSize: long.MaxValue,
            isLazy: false,
            aes: null,
            packedParts: [25, 25]);

        Assert.Equal(1, (await MultipartFileSizeRepairService.RepairAsync(_context, CancellationToken.None)).Repaired);
        Assert.Equal(0, (await MultipartFileSizeRepairService.RepairAsync(_context, CancellationToken.None)).Repaired);
    }

    [Fact]
    public async Task RepairAsync_SkipsStillLazyRows()
    {
        var blobId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await SeedMultipartAsync(
            itemId,
            blobId,
            fileSize: long.MaxValue,
            isLazy: true,
            aes: null,
            packedParts: [40]);

        var result = await MultipartFileSizeRepairService.RepairAsync(_context, CancellationToken.None);
        Assert.Equal(0, result.Repaired);
        Assert.Equal(1, result.SkippedLazy);

        _context.ChangeTracker.Clear();
        var item = await _context.Items.AsNoTracking().SingleAsync(x => x.Id == itemId);
        Assert.Equal(long.MaxValue, item.FileSize);
    }

    [Fact]
    public async Task RepairAsync_SkipsAesSentinelWithoutGuessing()
    {
        var blobId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await SeedMultipartAsync(
            itemId,
            blobId,
            fileSize: long.MaxValue,
            isLazy: false,
            aes: new AesParams { Key = new byte[16], Iv = new byte[16], DecodedSize = long.MaxValue },
            packedParts: [40, 60]);

        var result = await MultipartFileSizeRepairService.RepairAsync(_context, CancellationToken.None);
        Assert.Equal(0, result.Repaired);
        Assert.Equal(1, result.SkippedUnsafe);

        _context.ChangeTracker.Clear();
        var item = await _context.Items.AsNoTracking().SingleAsync(x => x.Id == itemId);
        Assert.Equal(long.MaxValue, item.FileSize);
    }

    [Fact]
    public async Task RepairAsync_UsesFiniteAesDecodedSize()
    {
        var blobId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await SeedMultipartAsync(
            itemId,
            blobId,
            fileSize: long.MaxValue,
            isLazy: false,
            aes: new AesParams { Key = new byte[16], Iv = new byte[16], DecodedSize = 80 },
            packedParts: [96]); // ciphertext longer than plaintext

        var result = await MultipartFileSizeRepairService.RepairAsync(_context, CancellationToken.None);
        Assert.Equal(1, result.Repaired);

        _context.ChangeTracker.Clear();
        var item = await _context.Items.AsNoTracking().SingleAsync(x => x.Id == itemId);
        Assert.Equal(80, item.FileSize);
    }

    private async Task SeedMultipartAsync(
        Guid itemId,
        Guid blobId,
        long fileSize,
        bool isLazy,
        AesParams? aes,
        int[] packedParts)
    {
        var multipart = new DavMultipartFile
        {
            Id = blobId,
            Metadata = new DavMultipartFile.Meta
            {
                AesParams = aes,
                IsLazy = isLazy,
                PathInArchive = "movie.mkv",
                FileParts = packedParts.Select((count, i) => new DavMultipartFile.FilePart
                {
                    SegmentIds = [$"seg-{i}"],
                    SegmentIdByteRange = LongRange.FromStartAndSize(0, count),
                    FilePartByteRange = LongRange.FromStartAndSize(0, count),
                }).ToArray(),
                PendingParts = isLazy
                    ?
                    [
                        new DavMultipartFile.PendingPart
                        {
                            SegmentIds = ["pending"],
                            SegmentIdByteRange = LongRange.FromStartAndSize(0, 10),
                            EstimatedDataSize = 10,
                        }
                    ]
                    : [],
            }
        };
        await BlobStore.WriteBlob(blobId, multipart);

        _context.Items.Add(new DavItem
        {
            Id = itemId,
            IdPrefix = itemId.ToString("N")[..5],
            CreatedAt = DateTime.UtcNow,
            Name = "movie.mkv",
            FileSize = fileSize,
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.MultipartFile,
            Path = "/content/movie.mkv",
            FileBlobId = blobId,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }
}
