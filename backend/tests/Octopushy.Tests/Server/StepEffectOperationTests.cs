using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>
/// Which effect a finished step gets, by its name and its run's operation: a handler, or none, which leaves the step to
/// <c>ApplyEffectsAsync</c>'s switch. A handler that does not apply to an operation leaves that step to the switch whole, so no step
/// is handled twice. Every step a run factory makes is listed, with an operation the code does not know for the names whose effect
/// depends on it.
/// </summary>
public class StepEffectOperationTests
{
    private const string Unknown = "no-such-operation";
    private const string Switch = "switch";

    [Theory]
    [InlineData(Operations.Provision, StepNames.Plan, Switch)]
    [InlineData(Operations.Provision, StepNames.Approval, Switch)]
    [InlineData(Operations.Provision, StepNames.Unprotect, Switch)]
    [InlineData(Operations.Provision, StepNames.Apply, Switch)]
    [InlineData(Operations.Provision, StepNames.Verify, Switch)]
    [InlineData(Operations.Provision, StepNames.Reprotect, Switch)]
    [InlineData(Operations.Retire, StepNames.Plan, Switch)]
    [InlineData(Operations.Retire, StepNames.Approval, Switch)]
    [InlineData(Operations.Retire, StepNames.Unprotect, Switch)]
    [InlineData(Operations.Retire, StepNames.Apply, "ForgetOutputsEffect")]
    [InlineData(Operations.Retire, StepNames.VerifyAbsent, Switch)]
    [InlineData(Operations.Attach, StepNames.Approval, Switch)]
    [InlineData(Operations.Attach, StepNames.Verify, "AttachVerifyEffect")]
    [InlineData(Operations.Attach, StepNames.Protect, Switch)]
    [InlineData(Operations.Configure, StepNames.Preview, Switch)]
    [InlineData(Operations.Configure, StepNames.Approval, Switch)]
    [InlineData(Operations.Configure, StepNames.Playbook, "PlaybookEffect")]
    [InlineData(Operations.Configure, StepNames.OutcomeApproval, Switch)]
    [InlineData(Operations.Configure, StepNames.PlaybookApproved, "PlaybookEffect")]
    [InlineData(Operations.RotateAccess, StepNames.Preview, Switch)]
    [InlineData(Operations.RotateAccess, StepNames.Approval, Switch)]
    [InlineData(Operations.RotateAccess, StepNames.VerifyCurrent, "VerifyLoginEffect")]
    [InlineData(Operations.RotateAccess, StepNames.AuthorizeKey, "AuthorizeKeyEffect")]
    [InlineData(Operations.RotateAccess, StepNames.VerifyKey, "VerifyLoginEffect")]
    [InlineData(Operations.RotateAccess, StepNames.PromoteKey, Switch)]
    [InlineData(Operations.RotateAccess, StepNames.ApproveRemoval, Switch)]
    [InlineData(Operations.RotateAccess, StepNames.RemoveKey, Switch)]
    [InlineData(Operations.RotateAccess, StepNames.VerifyRemoval, Switch)]
    [InlineData(Operations.RotateAccess, StepNames.ConfirmRefused, "ConfirmRefusedEffect")]
    // A pipeline's approval has no stage; its other steps do, and their effects are the stage's, before any of this.
    [InlineData(Operations.Pipeline, StepNames.Approval, Switch)]
    // As the switch had it: an apply of anything but a provision forgets the outputs, and a verify of anything but an attach is
    // a provision's verify.
    [InlineData(Unknown, StepNames.Apply, "ForgetOutputsEffect")]
    [InlineData(Unknown, StepNames.Verify, Switch)]
    public void Each_step_of_each_operation_has_one_effect(string operation, string step, string expected) =>
        Assert.Equal(expected, RunEngine.StepEffectFor(step, operation)?.GetType().Name ?? Switch);

    [Fact]
    public void An_effect_that_applies_to_every_operation_is_the_one_its_name_has()
    {
        string[] operations = [Operations.Provision, Operations.Retire, Operations.Configure, Operations.Pipeline, Operations.RotateAccess, Operations.Attach, Unknown];
        Assert.All(StepTypes.All, type => Assert.All(operations, operation =>
        {
            var registered = RunEngine.StepEffectFor(type.Name);
            var effect = RunEngine.StepEffectFor(type.Name, operation);
            Assert.True(effect is null || ReferenceEquals(effect, registered));
            Assert.Equal(registered is not null && registered.AppliesTo(operation), effect is not null);
        }));
    }
}
