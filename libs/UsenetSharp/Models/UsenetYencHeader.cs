namespace UsenetSharp.Models;

public record UsenetYencHeader
{
    public required string FileName;
    public required long FileSize;
    public required int LineLength;
    public required int PartNumber;
    public required int TotalParts;
    public required long PartSize;
    public required long PartOffset;

    public bool IsFilePart => this.PartNumber > 0;
}
