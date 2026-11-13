using Octopushy.Server.Inventory;

namespace Octopushy.Server.Orchestration;

/// <summary>The loop that advances runs and assigns work, woken by events and every few seconds.</summary>
public sealed class Orchestrator(RunEngine engine, Planner planner, Agents.AgentUpdates updates, OrchestratorSignal signal, TimeProvider clock, ILogger<Orchestrator> logger)
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
            try
            {
                await engine.TickAsync(stoppingToken);
                await planner.ScheduleConfigurationsAsync(stoppingToken);
                await planner.SchedulePipelinesAsync(stoppingToken);
                await updates.AdvanceAsync(stoppingToken);
                if (clock.GetUtcNow() >= nextObserveCheck)
                {
                    nextObserveCheck = clock.GetUtcNow() + ObserveCheck;
                    await planner.ScheduleObservesAsync(stoppingToken);
                    await planner.FireSchedulesAsync(clock.GetUtcNow(), stoppingToken);
                    await planner.ExpireScheduledAsync(clock.GetUtcNow(), stoppingToken);
                }
            }
            // Only the server stopping ends the loop: a cancellation from anywhere else, such as a timeout, is one more failed pass.
            catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "The orchestrator loop failed; it will try again.");
            }

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
}
