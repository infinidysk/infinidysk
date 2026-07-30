namespace NzbWebDAV.UsenetMigration;

/// <summary>
/// Known values for migration <c>SourceType</c> columns. New sources add a constant here
/// and replace hard-coded string literals in store / provenance / planner code.
/// </summary>
public static class MigrationSourceTypes
{
    public const string Altmount = "altmount";
}
