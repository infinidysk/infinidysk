using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.WebDav.Requests;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreIdsCollection(
    string name,
    string currentPath,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager,
    LazyRarResolver lazyRarResolver,
    InFlightArticleBudget inFlightArticleBudget
) : BaseStoreReadonlyCollection
{
    public override string Name => name;
    public override string UniqueKey => currentPath;
    public override DateTime CreatedAt => WebDavCreatedAtUtil.Fallback;

    private const string Alphabet = "0123456789abcdef";

    private readonly string[] _currentPathParts = currentPath.Split(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar,
        StringSplitOptions.RemoveEmptyEntries
    );

    protected override async Task<IStoreItem?> GetItemAsync(GetItemRequest request)
    {
        var (dir, ctx, db, usenet, config, lazy, budget) =
            (request.Name, httpContext, dbClient, usenetClient, configManager, lazyRarResolver,
                inFlightArticleBudget);
        if (_currentPathParts.Length < DavItem.IdPrefixLength)
        {
            if (request.Name.Length != 1) return null;
            if (!Alphabet.Contains(request.Name[0], StringComparison.Ordinal)) return null;
            return new DatabaseStoreIdsCollection(
                dir, Path.Join(currentPath, dir), ctx, db, usenet, config, lazy, budget);
        }

        var item = await dbClient.GetFileById(request.Name).ConfigureAwait(false);
        return item == null
            ? null
            : new DatabaseStoreIdFile(item, ctx, dbClient, usenet, config, lazy, budget);
    }

    protected override async IAsyncEnumerable<IStoreItem> GetAllItemsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (ctx, db, usenet, config, lazy, budget) =
            (httpContext, dbClient, usenetClient, configManager, lazyRarResolver, inFlightArticleBudget);
        if (_currentPathParts.Length < DavItem.IdPrefixLength)
        {
            foreach (var segment in Alphabet)
            {
                yield return new DatabaseStoreIdsCollection(
                    segment.ToString(), Path.Join(currentPath, segment.ToString()), ctx, db, usenet, config, lazy,
                    budget);
            }

            yield break;
        }

        var idPrefix = string.Join("", _currentPathParts);
        foreach (var item in await dbClient.GetFilesByIdPrefix(idPrefix).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new DatabaseStoreIdFile(item, ctx, dbClient, usenet, config, lazy, budget);
        }
    }
}
