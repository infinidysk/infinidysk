namespace NzbWebDAV.Exceptions;

/// <summary>
/// Thrown when one volume of a multipart file delivers fewer bytes than the stored
/// metadata says it holds. Zeros cannot stand in for the shortfall: the archive data
/// after this point is genuinely absent, so continuing would hand the player a file
/// that looks complete and plays back corrupt.
/// </summary>
public class IncompleteMultipartPartException(string message)
    : NonRetryableDownloadException(message)
{
}
