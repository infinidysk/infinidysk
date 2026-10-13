namespace NzbWebDAV.Exceptions;

internal sealed class UsenetMismatchedArticleException(string segmentId, string message)
    : UsenetArticleNotFoundException(segmentId, message)
{
    public UsenetMismatchedArticleException(
        string segmentId,
        int actualPartNumber,
        int actualTotalParts,
        int expectedTotalParts)
        : this(
            segmentId,
            $"Provider returned yEnc part {actualPartNumber}/{actualTotalParts} " +
            $"for a file with {expectedTotalParts} parts.")
    {
    }
}