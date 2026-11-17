using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>A step's effect is found by its exact name, only for a step type the registry has.</summary>
public class StepEffectTests
{
    [Fact]
    public void Every_effect_belongs_to_a_step_type_and_is_found_by_its_name()
    {
        Assert.All(RunEngine.StepEffects, entry =>
        {
            Assert.NotNull(StepTypes.Find(entry.Key));
            Assert.Same(entry.Value, RunEngine.StepEffectFor(entry.Key));
        });
    }

    [Theory]
    [InlineData("no-such-step")]
    [InlineData("")]
    [InlineData("Playbook")]
    [InlineData(" playbook")]
    public void A_name_no_step_type_has_has_no_effect(string name) => Assert.Null(RunEngine.StepEffectFor(name));
}
