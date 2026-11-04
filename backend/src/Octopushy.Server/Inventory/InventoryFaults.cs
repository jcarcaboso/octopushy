namespace Octopushy.Server.Inventory;

/// <summary>
/// A test seam: a fault to inject while a revision is applied, between recording the desired inputs and proposing the runs,
/// where a crash used to leave a revision recorded without its runs. Nothing sets it outside tests.
/// </summary>
public sealed class InventoryFaults
{
    public Func<Task>? BeforeRuns { get; set; }
}
