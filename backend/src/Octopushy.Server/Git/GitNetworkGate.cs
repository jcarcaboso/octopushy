namespace Octopushy.Server.Git;

/// <summary>
/// One writer on the lab repository's branch at a time: a push with the records around it, or a sync's pick, fetch and
/// reconciliation of changes. It is held without the engine's gate, so runs advance and results are recorded while Git
/// waits on the network. Take it before the engine's gate, never while holding it.
/// </summary>
public sealed class GitNetworkGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Held(_gate);
    }

    private sealed class Held(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
