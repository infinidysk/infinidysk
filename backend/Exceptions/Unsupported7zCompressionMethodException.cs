namespace NzbWebDAV.Exceptions;

// ReSharper disable once InconsistentNaming
public class Unsupported7zCompressionMethodException(
	string message = "Compressed 7z archives are intentionally unsupported by InfiniDysk: direct streaming and seeking require uncompressed (Copy/store) archive contents. This is a design limitation, not an application error. Choose a different release with uncompressed archives or direct media files.")
	: NonRetryableDownloadException(message)
{
}
