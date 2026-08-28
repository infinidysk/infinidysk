namespace NzbWebDAV.Services;

/// <summary>
/// Stops benchmark startup until background health work admitted before the
/// benchmark pause has released its operation and physical connection leases.
/// </summary>
public interface IHealthCheckQuiescence
{
    Task WaitForQuiescenceAsync(CancellationToken cancellationToken);
}
