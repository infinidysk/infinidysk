namespace NzbWebDAV.Tests.TestUtils;

/// <summary>
/// Tests that swap the process-wide <c>StreamTrace</c> buffer belong to this collection so
/// parallel classes cannot replace or disable each other's buffer mid-test.
/// </summary>
[CollectionDefinition(nameof(StreamTraceCollection))]
public class StreamTraceCollection;
