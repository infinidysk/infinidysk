namespace NzbWebDAV.Database.Models;

public sealed class SetupWizardState
{
    public const int SingletonId = 1;

    public int Id { get; init; } = SingletonId;
    public int WizardVersion { get; set; }
    public SetupWizardDisposition Disposition { get; set; }
    public string IngestionMethods { get; set; } = "[]";
    public DateTimeOffset UpdatedAt { get; set; }
}


public enum SetupWizardDisposition
{
    Completed = 1,
    Skipped = 2,
}