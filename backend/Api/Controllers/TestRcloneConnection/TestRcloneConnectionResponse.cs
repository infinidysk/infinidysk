namespace NzbWebDAV.Api.Controllers.TestRcloneConnection;

public class TestRcloneConnectionResponse : BaseApiResponse
{
    public bool Connected { get; set; }
    public long? ReadAheadBytes { get; set; }
    public string? CacheMode { get; set; }
    public string? VfsInspectionError { get; set; }
    public string? LastInvalidationError { get; set; }
    public DateTimeOffset? LastInvalidationErrorAt { get; set; }
}
