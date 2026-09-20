namespace NzbWebDAV.Clients.Usenet.Contexts;

internal sealed record TransferAdmissionFailoverContext(
    Func<bool> HasAlternativeCapacity,
    TimeSpan WaitTimeout)
{
    internal static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(15);
}
