namespace NzbWebDAV.Streams;

/// <summary>
/// Implemented by streams that hand out article bytes before the article's trailer is
/// validated, and by wrappers that forward to the stream they are reading. A reader that
/// stops before end of stream awaits it before releasing its final bytes.
/// </summary>
internal interface IDeliveredBytesValidation
{
    /// <summary>Completes once every byte returned so far is validated; throws if one is not.</summary>
    ValueTask ValidateDeliveredAsync(CancellationToken cancellationToken);
}

internal static class DeliveredBytesValidation
{
    public static ValueTask ValidateDeliveredAsync(this Stream? stream, CancellationToken cancellationToken) =>
        stream is IDeliveredBytesValidation validation
            ? validation.ValidateDeliveredAsync(cancellationToken)
            : ValueTask.CompletedTask;
}
