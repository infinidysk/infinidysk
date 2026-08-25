namespace NzbWebDAV.Tests.Database;

/// <summary>
/// Serializes tests that mutate DatabaseContractWriter's process-wide static
/// factories and paths, so a seam saved in one test class is never the mutated
/// value left behind by another.
/// </summary>
[CollectionDefinition(nameof(DatabaseContractWriterCollection), DisableParallelization = true)]
public sealed class DatabaseContractWriterCollection
{
}
