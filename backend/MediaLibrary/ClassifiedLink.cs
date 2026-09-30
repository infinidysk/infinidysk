namespace NzbWebDAV.MediaLibrary;

public sealed record ClassifiedLink(
    string RelativeLinkPath,
    string TargetText,
    LibraryMappingType MappingType,
    Guid? DavItemId);
