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

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string GuidText(Guid value) => value.ToString("D").ToUpperInvariant();
}
