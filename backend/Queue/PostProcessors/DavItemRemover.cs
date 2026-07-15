using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Queue.PostProcessors;

public static class DavItemRemover
{
    public static void Remove(DavDatabaseClient dbClient, DavItem davItem)
    {
        if (davItem.SubType == DavItem.ItemSubType.NzbFile)
        {
            dbClient.Ctx.BlobNzbFiles.RemoveAll(x => x.Id == davItem.FileBlobId);
            var file = dbClient.Ctx.ChangeTracker.Entries<DavNzbFile>()
                .Where(x => x.State == EntityState.Added)
                .Select(x => x.Entity)
                .FirstOrDefault(x => x.Id == davItem.Id);
            if (file is not null)
                dbClient.Ctx.NzbFiles.Remove(file);
        }

        else if (davItem.SubType == DavItem.ItemSubType.RarFile)
        {
            dbClient.Ctx.BlobRarFiles.RemoveAll(x => x.Id == davItem.FileBlobId);
            var file = dbClient.Ctx.ChangeTracker.Entries<DavRarFile>()
                .Where(x => x.State == EntityState.Added)
                .Select(x => x.Entity)
                .FirstOrDefault(x => x.Id == davItem.Id);
            if (file is not null)
                dbClient.Ctx.RarFiles.Remove(file);
        }

        else if (davItem.SubType == DavItem.ItemSubType.MultipartFile)
        {
            dbClient.Ctx.BlobMultipartFiles.RemoveAll(x => x.Id == davItem.FileBlobId);
            var file = dbClient.Ctx.ChangeTracker.Entries<DavMultipartFile>()
                .Where(x => x.State == EntityState.Added)
                .Select(x => x.Entity)
                .FirstOrDefault(x => x.Id == davItem.Id);
            if (file is not null)
                dbClient.Ctx.MultipartFiles.Remove(file);
        }

        else
        {
            Log.Error("Error removing dav item.");
            return;
        }

        dbClient.Ctx.Items.Remove(davItem);
    }
}
