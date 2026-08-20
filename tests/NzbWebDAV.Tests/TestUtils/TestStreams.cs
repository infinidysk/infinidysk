namespace NzbWebDAV.Tests.TestUtils;

/// <summary>
/// Test helpers that construct streams whose ownership is transferred to the caller.
/// </summary>
internal static class TestStreams
{
    public static Stream Create(byte[] payload) => new MemoryStream(payload, writable: false);
}
