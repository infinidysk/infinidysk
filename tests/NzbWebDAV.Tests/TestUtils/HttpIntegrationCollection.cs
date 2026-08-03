namespace NzbWebDAV.Tests.TestUtils;

/// <summary>
/// Serializes the real application host because it mutates process-wide environment
/// variables and database option caches.
/// </summary>
[CollectionDefinition(nameof(HttpIntegrationCollection), DisableParallelization = true)]
public sealed class HttpIntegrationCollection
    : ICollectionFixture<NzbDavWebApplicationFactory>;
