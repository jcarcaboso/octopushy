using Octopushy.Server.Inventory;

namespace Octopushy.Server.Orchestration;

/// <summary>The loop that advances runs and assigns work, woken by events and every few seconds.</summary>
public sealed class Orchestrator(RunEngine engine, Planner planner, OrchestratorSignal signal, TimeProvider clock, ILogger<Orchestrator> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ObserveCheck = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextObserveCheck = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await engine.TickAsync(stoppingToken);
                if (clock.GetUtcNow() >= nextObserveCheck)
                {
                    nextObserveCheck = clock.GetUtcNow() + ObserveCheck;
                    await planner.ScheduleObservesAsync(stoppingToken);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
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
