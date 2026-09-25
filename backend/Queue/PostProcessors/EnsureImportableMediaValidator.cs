using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Queue.PostProcessors;

public class EnsureImportableMediaValidator(DavDatabaseClient dbClient)
{
    internal const string NestedRarInSevenZipFailureMessage =
        "No importable media files found. RAR members inside 7z archives are not expanded by InfiniDysk. " +
        "Choose a release with directly accessible media or a supported top-level archive.";

    public void ThrowIfValidationFails()
    {
        ThrowIfValidationFails(null);
    }

    internal void ThrowIfValidationFails(IReadOnlyList<BaseProcessor.Result>? processorResults)
    {
        if (IsValid())
            return;

        var containsNestedRar = processorResults?
            .OfType<SevenZipProcessor.Result>()
            .SelectMany(result => result.SevenZipFiles)
            .Any(file => FilenameUtil.IsRarFile(file.PathWithinArchive)) == true;

        throw new NoMediaFilesFoundException(containsNestedRar
            ? NestedRarInSevenZipFailureMessage
            : "No importable media files found.");
    }

    private bool IsValid()
    {
        return dbClient.Ctx.ChangeTracker.Entries<DavItem>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .Where(x => x.Type != DavItem.ItemType.Directory)
            .Any(x => FilenameUtil.IsMediaFile(x.Name));
    }
}
