using Octopushy.Server.Inventory;

namespace Octopushy.Server.Orchestration;

/// <summary>The loop that advances runs and assigns work, woken by events and every few seconds.</summary>
public sealed class Orchestrator(RunEngine engine, Planner planner, Agents.AgentUpdates updates, Agents.AgentPresence presence, OrchestratorSignal signal,
    TimeProvider clock, ILogger<Orchestrator> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ObserveCheck = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The server was away for an unknown time, and no agent could report meanwhile: each gets a full lease to
        // connect again before its work is taken for lost.
        try
        {
            await engine.RenewLeasesAsync(stoppingToken);
        }
        catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(e, "Renewing the leases of running work at startup failed; work whose agent has not reported in time is taken for lost.");
        }

        var nextObserveCheck = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            var observeDue = clock.GetUtcNow() >= nextObserveCheck;
            if (observeDue) nextObserveCheck = clock.GetUtcNow() + ObserveCheck;
            await RunOnceAsync(observeDue, stoppingToken);

            try
            {
                await signal.WaitAsync(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task RunOnceAsync(bool observeDue, CancellationToken cancellationToken)
    {
        await PhaseAsync("runs", () => engine.TickAsync(cancellationToken), cancellationToken);
        await PhaseAsync("configure scheduling", () => planner.ScheduleConfigurationsAsync(cancellationToken), cancellationToken);
        await PhaseAsync("pipeline scheduling", () => planner.SchedulePipelinesAsync(cancellationToken), cancellationToken);
        await PhaseAsync("agent updates", () => updates.AdvanceAsync(cancellationToken), cancellationToken);
        if (observeDue)
        {
            await PhaseAsync("observations", () => planner.ScheduleObservesAsync(cancellationToken), cancellationToken);
            await PhaseAsync("cron", () => planner.FireSchedulesAsync(clock.GetUtcNow(), cancellationToken), cancellationToken);
            await PhaseAsync("scheduled expiry", () => planner.ExpireScheduledAsync(clock.GetUtcNow(), cancellationToken), cancellationToken);
            await PhaseAsync("agent presence", () => presence.CheckAsync(cancellationToken), cancellationToken);
        }
    }

    private async Task PhaseAsync(string phase, Func<Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await action();
        }
        // A timeout is a failed phase; only shutdown cancels the loop.
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(e, "The orchestrator phase {Phase} failed; later phases will still run.", phase);
        }
    }
}
