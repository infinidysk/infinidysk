namespace NzbWebDAV.Tests.TestUtils;

/// <summary>
/// Shared-stream tests that exercise <see cref="NzbWebDAV.Streams.InFlightArticleBudget"/>
/// waiters and reclaim. Disable parallelization so a saturated-budget case cannot
/// interleave with other budget tests that share timing assumptions.
/// </summary>
[CollectionDefinition(nameof(SharedStreamCollection), DisableParallelization = true)]
public class SharedStreamCollection;
