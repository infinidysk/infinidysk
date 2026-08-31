namespace NzbWebDAV.Services;

public enum QueueCoordinatorState
{
    NotStarted,
    Running,
    Stopped,
    Faulted,
}

/// <summary>
/// Exposes only queue-coordinator lifecycle state to health reporting.
/// Queue mutations remain on IQueueCoordinator.
/// </summary>
public interface IQueueCoordinatorLiveness
{
    QueueCoordinatorState GetState();
}
