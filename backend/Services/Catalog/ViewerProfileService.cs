using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;

namespace NzbWebDAV.Services.Catalog;

/// <summary>
/// Viewer profiles: the streaming-style identities the login picker offers. Selecting a
/// profile is the whole sign-in, so profiles carry no credentials; the only secret in the
/// system stays the admin credential. Rows are create/list only — deletes would orphan
/// the watch history they own, which is why the schema keeps none.
/// </summary>
public sealed class ViewerProfileService(
    Func<DavDatabaseContext> contextFactory,
    TimeProvider timeProvider)
{
    /// <summary>Id of the seeded "Main" profile that admin sessions and legacy rows use.</summary>
    public const int DefaultProfileId = 1;

    internal const int MaxProfiles = 8;

    public async Task<IReadOnlyList<ViewerProfile>> ListAsync(CancellationToken ct)
    {
        await using var context = contextFactory();
        return await context.ViewerProfiles
            .AsNoTracking()
            .OrderBy(profile => profile.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<ViewerProfile> CreateAsync(string? name, CancellationToken ct)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
            throw CatalogException.Validation("Profile name is required.");
        if (trimmed.Length > ViewerProfile.MaxNameLength)
            throw CatalogException.Validation(
                $"Profile names can be at most {ViewerProfile.MaxNameLength} characters.");

        await using var context = contextFactory();
        var existing = await context.ViewerProfiles.CountAsync(ct).ConfigureAwait(false);
        if (existing >= MaxProfiles)
            throw CatalogException.Validation($"You can create up to {MaxProfiles} profiles.");

        var profile = new ViewerProfile
        {
            Name = trimmed,
            CreatedAtEpoch = timeProvider.GetUtcNow().ToUnixTimeSeconds(),
        };
        context.ViewerProfiles.Add(profile);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return profile;
    }
}
