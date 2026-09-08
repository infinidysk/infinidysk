namespace NzbWebDAV.Config;

/// <summary>
/// How much of each file a health check verifies.
/// <list type="bullet">
/// <item><see cref="Quick"/> STATs only a small fixed, stratified sample per file
/// regardless of size. A whole-posting loss (takedown or retention) removes every
/// article, so a couple of dozen head/tail/stride probes reliably separate an
/// intact posting from a gone one, at a fraction of the NNTP cost — useful for a
/// first full-library pass.</item>
/// <item><see cref="Standard"/> through <see cref="Deep"/> scale the sampling
/// curve; files up to the sample floor are still checked in full.</item>
/// <item><see cref="Complete"/> skips sampling and STATs every segment.</item>
/// </list>
/// </summary>
public enum HealthCheckDepth
{
    Quick,
    Standard,
    Enhanced,
    Deep,
    Complete,
}
