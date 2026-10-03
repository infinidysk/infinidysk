namespace NzbWebDAV.Queue.Inspection;

/// <summary>
/// Facts established by inspecting an NZB through the import planner without importing it.
/// <see cref="ManifestComplete"/> answers only "is the complete inner file namespace known?";
/// whether the content can actually be served is reported separately per archive through
/// <see cref="InspectedArchive.ContentAccess"/> and <see cref="InspectedArchive.StreamSupported"/>.
/// </summary>
public sealed class NzbInspectionReport
{
    /// <summary>True only when planning succeeded, no inspection limit was reached and no archive was left unexpanded.</summary>
    public bool ManifestComplete { get; init; }

    public required IReadOnlyList<InspectedArchive> Archives { get; init; }

    /// <summary>Planned outputs. Empty unless planning completed; never a partial list presented as authoritative.</summary>
    public required IReadOnlyList<InspectedFile> Files { get; init; }

    public required InspectionDiagnostics Diagnostics { get; init; }
}

public sealed class InspectedArchive
{
    /// <summary>Archive-set identity as used by the import planner (for example <c>set:1</c> or <c>nested:1</c>).</summary>
    public required string ArchiveSetId { get; init; }

    /// <summary><c>rar</c> or <c>7z</c>.</summary>
    public required string ArchiveType { get; init; }

    /// <summary>0 for archives posted in the NZB; 1+ for stored archives expanded out of another archive.</summary>
    public int NestingDepth { get; init; }

    /// <summary>See <see cref="InspectionEncryption"/>.</summary>
    public required string Encryption { get; init; }

    /// <summary>See <see cref="InspectionContentAccess"/>.</summary>
    public required string ContentAccess { get; init; }

    /// <summary>Whether InfiniDysk can stream this archive shape; <c>null</c> when not established.</summary>
    public bool? StreamSupported { get; init; }
}

public sealed class InspectedFile
{
    /// <summary>Name as posted, or as stored inside the archive. Authoritative for identifying content.</summary>
    public required string RawPath { get; init; }

    /// <summary>Name an import would plan to mount. Informational only: single videos may be renamed to the release name.</summary>
    public required string PlannedName { get; init; }

    public long Size { get; init; }

    /// <summary>Video extension from content sniffing or the raw name; <c>null</c> for non-video files.</summary>
    public string? MediaExtension { get; init; }

    /// <summary><c>par2</c>, <c>subject</c>, <c>yencHeader</c> or <c>archiveHeader</c>.</summary>
    public string? NameSource { get; init; }

    /// <summary>Owning archive set; <c>null</c> for files posted directly (including split video parts).</summary>
    public string? ArchiveSetId { get; init; }

    public int NestingDepth { get; init; }
}

public sealed class InspectionDiagnostics
{
    /// <summary>Files whose first segment is missing across all providers (observed, never inferred).</summary>
    public required IReadOnlyList<string> MissingFirstSegments { get; init; }

    public required IReadOnlyList<InspectionWarning> Warnings { get; init; }

    /// <summary>Why planning did not complete; <c>null</c> when it completed.</summary>
    public InspectionFailure? Failure { get; init; }

    /// <summary>Planning stages entered, in order.</summary>
    public required IReadOnlyList<string> Stages { get; init; }

    /// <summary>
    /// Article requests the inspection issued to the provider client (per segment, including
    /// requests that failed). Not physical provider attempts: failover and retries inside the
    /// client are not counted, and some requests may be served from its caches.
    /// </summary>
    public int ArticleRequests { get; init; }
}

public sealed class InspectionWarning
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Path { get; init; }
}

public sealed class InspectionFailure
{
    /// <summary>See <see cref="InspectionFailureKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Planning stage that was running when inspection stopped.</summary>
    public string? Stage { get; init; }

    public required string Message { get; init; }
}

public static class InspectionEncryption
{
    public const string None = "none";
    public const string Data = "data";
    public const string Unknown = "unknown";
}

public static class InspectionContentAccess
{
    public const string Plain = "plain";
    public const string PasswordRequired = "password_required";
    public const string PasswordValidated = "password_validated";
    public const string Unsupported = "unsupported";
    public const string Unknown = "unknown";
}

public static class InspectionFailureKinds
{
    public const string LimitExceeded = "limit_exceeded";
    public const string Timeout = "timeout";
    public const string MissingArticles = "missing_articles";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string Unsupported = "unsupported";
    public const string Password = "password";
    public const string Failed = "failed";
}

public static class InspectionWarningCodes
{
    public const string NestedArchiveOpaque = "nested_archive_opaque";
    public const string MissingFirstSegment = "missing_first_segment";
    public const string PasswordNotValidated = "password_not_validated";
}
