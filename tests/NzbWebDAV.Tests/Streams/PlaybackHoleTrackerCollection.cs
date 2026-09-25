namespace NzbWebDAV.Tests.Streams;

/// <summary>
/// Serializes tests that read or reset the process-wide <c>PlaybackHoleTracker</c>.
/// </summary>
[CollectionDefinition(nameof(PlaybackHoleTrackerCollection), DisableParallelization = true)]
public sealed class PlaybackHoleTrackerCollection;
