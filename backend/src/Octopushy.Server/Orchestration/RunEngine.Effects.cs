using System.Text.Json.Nodes;
using Octopushy.Server.Data;
using Octopushy.Server.Store;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// What a finished step of a guest's run establishes, one effect per step type that has one, chosen by the step's name. A step
/// type with no entry in <see cref="StepEffects"/> has no effect, and neither has a name no step type has. A new step type
/// with an effect adds its entry in <see cref="StepTypes"/>, its effect, and its line here.
/// </summary>
public sealed partial class RunEngine
{
    /// <summary>
    /// Records what a succeeded step established, under the engine's lock and in the unit of work that took its result.
    /// Called once the checks every plan goes through have passed and the run's resource is loaded.
    /// </summary>
    internal interface IStepEffect
    {
        /// <summary>
        /// Whether this is the step's effect in a run of <paramref name="operation"/>, a constant of <see cref="Operations"/> or a
        /// value the code does not know. When it is not, the step's effect is still in <c>ApplyEffectsAsync</c>'s switch.
        /// </summary>
        bool AppliesTo(string operation) => true;

        Task ApplyAsync(RunEngine engine, OctopushyDb db, VariableStore store, Run run, Step step, Resource resource, JsonNode? outputs,
            DateTimeOffset now, CancellationToken cancellationToken);
    }

    /// <summary>The effect of each step name that has one. Every key is a name in <see cref="StepTypes"/>.</summary>
    internal static readonly IReadOnlyDictionary<string, IStepEffect> StepEffects = new Dictionary<string, IStepEffect>(StringComparer.Ordinal)
    {
        [StepNames.Playbook] = new PlaybookEffect(),
        [StepNames.PlaybookApproved] = new PlaybookEffect(),
        [StepNames.VerifyCurrent] = new VerifyLoginEffect(),
        [StepNames.AuthorizeKey] = new AuthorizeKeyEffect(),
        [StepNames.VerifyKey] = new VerifyLoginEffect(),
        [StepNames.ConfirmRefused] = new ConfirmRefusedEffect(),
        [StepNames.Apply] = new ForgetOutputsEffect(),
        [StepNames.Verify] = new AttachVerifyEffect(),
    };

    /// <summary>
    /// The effect registered for <paramref name="name"/>, matched exactly, whatever the run's operation; null for a step type
    /// without one, or a name no step has. A finished step's effect is <see cref="StepEffectFor(string, string)"/>.
    /// </summary>
    internal static IStepEffect? StepEffectFor(string name) => StepEffects.GetValueOrDefault(name);

    /// <summary>
    /// The effect of <paramref name="name"/> in a run of <paramref name="operation"/>: null when the step type has none, or its
    /// effect does not apply to that operation, so <c>ApplyEffectsAsync</c>'s switch decides. One or the other, never both.
    /// </summary>
    internal static IStepEffect? StepEffectFor(string name, string operation) =>
        StepEffectFor(name) is { } effect && effect.AppliesTo(operation) ? effect : null;

    /// <summary>A configure run's playbook, and its approved rerun, finished: <see cref="ApplyConfigureEffectsAsync"/>.</summary>
    private sealed class PlaybookEffect : IStepEffect
    {
        public async Task ApplyAsync(RunEngine engine, OctopushyDb db, VariableStore store, Run run, Step step, Resource resource, JsonNode? outputs,
            DateTimeOffset now, CancellationToken cancellationToken)
        {
            await engine.ApplyConfigureEffectsAsync(db, run, step, cancellationToken);
        }
    }

    /// <summary>
    /// <see cref="StepNames.Apply"/> of any run but a provision's, a retire's among them: what the machine's outputs were is gone.
    /// A provision's apply is in the switch.
    /// </summary>
    private sealed class ForgetOutputsEffect : IStepEffect
    {
        public bool AppliesTo(string operation) => operation != Operations.Provision;

        public Task ApplyAsync(RunEngine engine, OctopushyDb db, VariableStore store, Run run, Step step, Resource resource, JsonNode? outputs,
            DateTimeOffset now, CancellationToken cancellationToken)
        {
            resource.Outputs = null;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// <see cref="StepNames.Verify"/> of an attach run: the machine is at its VMID, on its host, of its kind. Nothing is bound here;
    /// that waits for protect. Any other run's verify, a provision's, is in the switch.
    /// </summary>
    private sealed class AttachVerifyEffect : IStepEffect
    {
        public bool AppliesTo(string operation) => operation == Operations.Attach;

        public Task ApplyAsync(RunEngine engine, OctopushyDb db, VariableStore store, Run run, Step step, Resource resource, JsonNode? outputs,
            DateTimeOffset now, CancellationToken cancellationToken)
        {
            resource.VerifiedAt = now;
            return Task.CompletedTask;
        }
    }
}
