namespace NzbWebDAV.Database.Models;

/// <summary>One viewer profile: a streaming-style identity that owns its own watch history.</summary>
public sealed class ViewerProfile
{
    public const int MaxNameLength = 32;

    public int Id { get; set; }
    public string Name { get; set; } = "";
    public long CreatedAtEpoch { get; set; }
}
