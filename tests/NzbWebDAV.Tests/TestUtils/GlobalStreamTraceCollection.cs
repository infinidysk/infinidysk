namespace NzbWebDAV.Tests.TestUtils;

/// <summary>
/// Tests that swap the global <c>StreamTrace</c> buffer belong to this collection so
/// parallel classes do not record into, or restore over, each other's buffer.
/// </summary>
[CollectionDefinition(nameof(GlobalStreamTraceCollection))]
public class GlobalStreamTraceCollection;
