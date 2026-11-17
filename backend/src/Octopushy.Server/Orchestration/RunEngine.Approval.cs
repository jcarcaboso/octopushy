using Octopushy.Server.Data;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// Whether a run's approval step waits for a person, and why: one decider per kind of run, chosen by the kind its context
/// stores. A new kind of run adds a decider and its entry in <see cref="ApprovalDeciders"/>.
/// </summary>
public sealed partial class RunEngine
{
    /// <summary>Decides a run's pending approval step, under the engine's lock and with its <see cref="OctopushyDb"/>.</summary>
    internal interface IApprovalDecider
    {
        /// <summary>
        /// Approves <paramref name="step"/>, holds it for a person and says why, or fails the run. Returns true when the step
        /// is settled and the run goes on.
        /// </summary>
        Task<bool> DecideAsync(RunEngine engine, OctopushyDb db, Run run, Step step, RunContext context, CancellationToken cancellationToken);
    }

    /// <summary>The decider of each kind of run in <see cref="RunKinds"/>.</summary>
    internal static readonly IReadOnlyDictionary<string, IApprovalDecider> ApprovalDeciders = new Dictionary<string, IApprovalDecider>(StringComparer.Ordinal)
    {
        [RunKinds.Provision] = new ProvisionApproval(),
        [RunKinds.Configure] = new ConfigureApproval(),
        [RunKinds.Pipeline] = new PipelineApproval(),
    };

    /// <summary>The decider of <paramref name="context"/>'s kind. A kind without one is an error, never a run that needs no approval.</summary>
    internal static IApprovalDecider ApprovalDeciderFor(RunContext context) =>
        context.Kind is { } kind && ApprovalDeciders.TryGetValue(kind, out var decider)
            ? decider
            : throw new InvalidOperationException($"No approval decider for the run kind '{context.Kind}'.");

    /// <summary>An OpenTofu plan of the guest's machine: <see cref="DecideApprovalAsync"/>.</summary>
    private sealed class ProvisionApproval : IApprovalDecider
    {
        public Task<bool> DecideAsync(RunEngine engine, OctopushyDb db, Run run, Step step, RunContext context, CancellationToken cancellationToken) =>
            engine.DecideApprovalAsync(db, run, step, context, cancellationToken);
    }

    /// <summary>An Ansible preview, a declared outcome or an access decision: <see cref="DecideConfigureApprovalAsync"/>.</summary>
    private sealed class ConfigureApproval : IApprovalDecider
    {
        public Task<bool> DecideAsync(RunEngine engine, OctopushyDb db, Run run, Step step, RunContext context, CancellationToken cancellationToken) =>
            engine.DecideConfigureApprovalAsync(db, run, step, context, cancellationToken);
    }

    /// <summary>The document that covers every stage: <see cref="DecidePipelineApprovalAsync"/>.</summary>
    private sealed class PipelineApproval : IApprovalDecider
    {
        public Task<bool> DecideAsync(RunEngine engine, OctopushyDb db, Run run, Step step, RunContext context, CancellationToken cancellationToken) =>
            engine.DecidePipelineApprovalAsync(db, run, step, context, cancellationToken);
    }
}
