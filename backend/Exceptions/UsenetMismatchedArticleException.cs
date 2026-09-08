namespace NzbWebDAV.Exceptions;

internal sealed class UsenetMismatchedArticleException(
    string segmentId,
    int actualPartNumber,
    int actualTotalParts,
    int expectedTotalParts)
    : UsenetArticleNotFoundException(
        segmentId,
        $"Provider returned yEnc part {actualPartNumber}/{actualTotalParts} " +
        $"for a file with {expectedTotalParts} parts.");