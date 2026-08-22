namespace NzbWebDAV.Exceptions;

/// <summary>
/// Fatal startup error when a pending migration finds a schema object that already
/// exists. Carries recovery guidance instead of exposing an EF Core stack trace.
/// </summary>
public sealed class DatabaseMigrationConflictException(Exception innerException)
    : Exception(
        "Database migration could not continue because a schema object already exists. " +
        "Back up /config before making changes, then restore a pre-upgrade backup or remove " +
        "the conflicting object and restart. See https://github.com/infinidysk/infinidysk/issues/1104.",
        innerException)
{
}
