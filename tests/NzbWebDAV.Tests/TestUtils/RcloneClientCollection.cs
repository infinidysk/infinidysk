namespace NzbWebDAV.Tests.TestUtils;

/// <summary>
/// Serializes tests that drive <c>RcloneClient</c>. The client exposes
/// process-wide statics (<c>Current</c>, <c>TestHandler</c>, <c>BackoffOverride</c>),
/// so two test classes running in parallel would install each other's fake HTTP
/// handlers and fail intermittently.
/// </summary>
[CollectionDefinition(nameof(RcloneClientCollection), DisableParallelization = true)]
public sealed class RcloneClientCollection;
