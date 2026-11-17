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
        Task ApplyAsync(RunEngine engine, OctopushyDb db, VariableStore store, Run run, Step step, Resource resource, JsonNode? outputs,
            DateTimeOffset now, CancellationToken cancellationToken);
    }

    /// <summary>The effect of each step name that has one. Every key is a name in <see cref="StepTypes"/>.</summary>
    internal static readonly IReadOnlyDictionary<string, IStepEffect> StepEffects = new Dictionary<string, IStepEffect>(StringComparer.Ordinal);

    /// <summary>The effect of <paramref name="name"/>, matched exactly; null for a step type without one, or a name no step has.</summary>
    internal static IStepEffect? StepEffectFor(string name) => StepEffects.GetValueOrDefault(name);
}
