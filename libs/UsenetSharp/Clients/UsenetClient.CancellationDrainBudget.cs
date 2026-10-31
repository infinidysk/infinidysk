namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    // Created per BODY operation and shared by every response of a batch. The deadline starts
    // at the first cancellation drain, not during healthy transfer or once per response.
    private sealed class CancellationDrainBudget(TimeSpan? timeout) : IDisposable
    {
        private CancellationTokenSource? _deadline;

        internal CancellationToken Token
        {
            get
            {
                if (timeout is null) return CancellationToken.None;
                if (_deadline is null)
                {
                    _deadline = new CancellationTokenSource();
                    if (timeout <= TimeSpan.Zero) _deadline.Cancel();
                    else _deadline.CancelAfter(timeout.Value);
                }
                return _deadline.Token;
            }
        }

        public void Dispose() => _deadline?.Dispose();
    }
}
