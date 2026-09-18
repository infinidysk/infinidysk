namespace NzbWebDAV.Exceptions;

public class UnsupportedRarCompressionMethodException(
	string message = "Compressed RAR archives are intentionally unsupported by InfiniDysk: direct streaming and seeking require uncompressed (stored/m0) archive contents. This is a design limitation, not an application error. Choose a different release with uncompressed archives or direct media files.")
	: NonRetryableDownloadException(message)
{
}
