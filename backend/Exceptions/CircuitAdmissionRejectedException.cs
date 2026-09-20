namespace NzbWebDAV.Exceptions;

internal sealed class CircuitAdmissionRejectedException()
    : RetryableDownloadException(
        "NNTP provider circuit is open or another half-open probe is already in flight.");
