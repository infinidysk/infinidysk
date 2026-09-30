using NzbWebDAV.MediaLibrary;

namespace NzbWebDAV.Database.Models;

/// <summary>
/// One row per discovered library symlink/STRM under <c>media.library-dir</c>.
/// <c>LinkPath</c> is relative to the library root and is the identity.
/// <c>DavItemId</c> is set only for verified internal <c>/.ids</c> targets.
/// External links (no InfiniDysk identity) have a null <c>DavItemId</c>.
/// </summary>
public class LibraryLinkMap
{
    public Guid Id { get; set; }
    public Guid? DavItemId { get; set; }
    public string LinkPath { get; set; } = null!;
    public string TargetText { get; set; } = null!;
    public LibraryMappingType MappingType { get; set; }
    public LibraryLinkStatus Status { get; set; }
    public long? Size { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
}
