namespace NzbWebDAV.Database.Models.Metrics;

public class ProviderQuotaUsage
{
    public string Provider { get; set; } = null!;
    public long BytesUsed { get; set; }
    public long ResetAt { get; set; }
    public long UpdatedAt { get; set; }
}