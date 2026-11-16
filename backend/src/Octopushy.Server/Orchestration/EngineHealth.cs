namespace Octopushy.Server.Orchestration;

/// <summary>A running process is ready only while its engine keeps completing ticks.</summary>
public sealed class EngineHealth(TimeProvider clock)
{
    private long _lastTick;

    public void TickCompleted() => Interlocked.Exchange(ref _lastTick, clock.GetUtcNow().UtcTicks);

    public bool IsFresh
    {
        get
        {
            var last = Interlocked.Read(ref _lastTick);
            return last != 0 && clock.GetUtcNow() - new DateTimeOffset(last, TimeSpan.Zero) <= TimeSpan.FromMinutes(1);
        }
    }
}
