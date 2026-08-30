using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Database;

internal static class HistoricalDavItemSeeder
{
    public static async Task SeedAsync(DavDatabaseContext context, IEnumerable<DavItem> items)
    {
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO DavItems (
                Id, IdPrefix, CreatedAt, ParentId, Name, FileSize, Type, SubType, Path,
                ReleaseDate, LastHealthCheck, NextHealthCheck, HistoryItemId, FileBlobId, NzbBlobId)
            VALUES (
                $id, $idPrefix, $createdAt, $parentId, $name, $fileSize, $type, $subType, $path,
                $releaseDate, $lastHealthCheck, $nextHealthCheck, $historyItemId, $fileBlobId, $nzbBlobId);
            """;

        foreach (var item in items)
        {
            command.Parameters.Clear();
            Add(command, "$id", GuidText(item.Id));
            Add(command, "$idPrefix", item.IdPrefix);
            Add(command, "$createdAt", item.CreatedAt);
            Add(command, "$parentId", item.ParentId is { } parentId ? GuidText(parentId) : null);
            Add(command, "$name", item.Name);
            Add(command, "$fileSize", item.FileSize);
            Add(command, "$type", (int)item.Type);
            Add(command, "$subType", (int)item.SubType);
            Add(command, "$path", item.Path);
            Add(command, "$releaseDate", item.ReleaseDate?.ToUnixTimeSeconds());
            Add(command, "$lastHealthCheck", item.LastHealthCheck?.ToUnixTimeSeconds());
            Add(command, "$nextHealthCheck", item.NextHealthCheck?.ToUnixTimeSeconds());
            Add(command, "$historyItemId", item.HistoryItemId is { } historyItemId ? GuidText(historyItemId) : null);
            Add(command, "$fileBlobId", item.FileBlobId is { } fileBlobId ? GuidText(fileBlobId) : null);
            Add(command, "$nzbBlobId", item.NzbBlobId is { } nzbBlobId ? GuidText(nzbBlobId) : null);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public static async Task SeedHistoryItemsAsync(DavDatabaseContext context, IEnumerable<HistoryItem> items)
    {
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO HistoryItems (
                Id, CreatedAt, FileName, JobName, Category, DownloadStatus, TotalSegmentBytes,
                DownloadTimeSeconds, FailMessage, DownloadDirId, NzbBlobId, IndexerName,
                ContentGroupKey, LastPlayedAt)
            VALUES (
                $id, $createdAt, $fileName, $jobName, $category, $downloadStatus, $totalSegmentBytes,
                $downloadTimeSeconds, $failMessage, $downloadDirId, $nzbBlobId, $indexerName,
                $contentGroupKey, $lastPlayedAt);
            """;

        foreach (var item in items)
        {
            command.Parameters.Clear();
            Add(command, "$id", GuidText(item.Id));
            Add(command, "$createdAt", item.CreatedAt);
            Add(command, "$fileName", item.FileName);
            Add(command, "$jobName", item.JobName);
            Add(command, "$category", item.Category);
            Add(command, "$downloadStatus", (int)item.DownloadStatus);
            Add(command, "$totalSegmentBytes", item.TotalSegmentBytes);
            Add(command, "$downloadTimeSeconds", item.DownloadTimeSeconds);
            Add(command, "$failMessage", item.FailMessage);
            Add(command, "$downloadDirId", item.DownloadDirId is { } downloadDirId ? GuidText(downloadDirId) : null);
            Add(command, "$nzbBlobId", item.NzbBlobId is { } nzbBlobId ? GuidText(nzbBlobId) : null);
            Add(command, "$indexerName", item.IndexerName);
            Add(command, "$contentGroupKey", item.ContentGroupKey);
            Add(command, "$lastPlayedAt", item.LastPlayedAt?.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public static async Task SeedQueueItemsAsync(DavDatabaseContext context, IEnumerable<QueueItem> items)
    {
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO QueueItems (
                Id, CreatedAt, SortOrder, FileName, JobName, NzbFileSize, TotalSegmentBytes,
                Category, Priority, PostProcessing, PauseUntil, IndexerName, ContentGroupKey)
            VALUES (
                $id, $createdAt, $sortOrder, $fileName, $jobName, $nzbFileSize, $totalSegmentBytes,
                $category, $priority, $postProcessing, $pauseUntil, $indexerName, $contentGroupKey);
            """;

        foreach (var item in items)
        {
            command.Parameters.Clear();
            Add(command, "$id", GuidText(item.Id));
            Add(command, "$createdAt", item.CreatedAt);
            Add(command, "$sortOrder", item.SortOrder);
            Add(command, "$fileName", item.FileName);
            Add(command, "$jobName", item.JobName);
            Add(command, "$nzbFileSize", item.NzbFileSize);
            Add(command, "$totalSegmentBytes", item.TotalSegmentBytes);
            Add(command, "$category", item.Category);
            Add(command, "$priority", (int)item.Priority);
            Add(command, "$postProcessing", (int)item.PostProcessing);
            Add(command, "$pauseUntil", item.PauseUntil);
            Add(command, "$indexerName", item.IndexerName);
            Add(command, "$contentGroupKey", item.ContentGroupKey);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string GuidText(Guid value) => value.ToString("D").ToUpperInvariant();
}
