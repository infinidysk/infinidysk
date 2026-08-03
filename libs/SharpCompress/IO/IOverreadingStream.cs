namespace SharpCompress.IO;

/// <summary>
/// Implemented by decompression streams that may read more bytes from the underlying
/// stream than the compressed data actually required. ReturnOverread() pushes the
/// unconsumed bytes back (rewinds the buffered underlying stream) so outer readers
/// see the correct position.
/// </summary>
internal interface IOverreadingStream
{
    void ReturnOverread();
}
