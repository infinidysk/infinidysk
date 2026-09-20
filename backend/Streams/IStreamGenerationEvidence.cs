namespace NzbWebDAV.Streams;

/// <summary>Opaque identity and live validity for streams whose source can be replaced.</summary>
public interface IStreamGenerationEvidence
{
    string GenerationIdentity { get; }
    bool IsSourceCurrent { get; }
}
