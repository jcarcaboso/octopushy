using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>
/// Which effect a finished step gets, by its name and its run's operation: one handler, or none. A name whose effect depends on the
/// operation has one handler per operation it applies to, and no two apply to the same one, so no step is handled twice. Every
/// step a run factory makes is listed, with an operation the code does not know for the names whose effect depends on it.
/// </summary>
public class StepEffectOperationTests
{
    private const string Unknown = "no-such-operation";
    private const string None = "none";

    [Theory]
    [InlineData(Operations.Provision, StepNames.Plan, "PlanEffect")]
    [InlineData(Operations.Provision, StepNames.Approval, None)]
    [InlineData(Operations.Provision, StepNames.Unprotect, None)]
    [InlineData(Operations.Provision, StepNames.Apply, "ProvisionApplyEffect")]
    [InlineData(Operations.Provision, StepNames.Verify, "ProvisionVerifyEffect")]
    [InlineData(Operations.Provision, StepNames.Reprotect, None)]
    [InlineData(Operations.Retire, StepNames.Plan, "PlanEffect")]
    [InlineData(Operations.Retire, StepNames.Approval, None)]
    [InlineData(Operations.Retire, StepNames.Unprotect, None)]
    [InlineData(Operations.Retire, StepNames.Apply, "ForgetOutputsEffect")]
    [InlineData(Operations.Retire, StepNames.VerifyAbsent, "VerifyAbsentEffect")]
    [InlineData(Operations.Attach, StepNames.Approval, None)]
    [InlineData(Operations.Attach, StepNames.Verify, "AttachVerifyEffect")]
    [InlineData(Operations.Attach, StepNames.Protect, "AttachProtectEffect")]
    [InlineData(Operations.Configure, StepNames.Preview, None)]
    [InlineData(Operations.Configure, StepNames.Approval, None)]
    [InlineData(Operations.Configure, StepNames.Playbook, "PlaybookEffect")]
    [InlineData(Operations.Configure, StepNames.OutcomeApproval, None)]
    [InlineData(Operations.Configure, StepNames.PlaybookApproved, "PlaybookEffect")]
    [InlineData(Operations.RotateAccess, StepNames.Preview, None)]
    [InlineData(Operations.RotateAccess, StepNames.Approval, None)]
    [InlineData(Operations.RotateAccess, StepNames.VerifyCurrent, "VerifyLoginEffect")]
    [InlineData(Operations.RotateAccess, StepNames.AuthorizeKey, "AuthorizeKeyEffect")]
    [InlineData(Operations.RotateAccess, StepNames.VerifyKey, "VerifyLoginEffect")]
    [InlineData(Operations.RotateAccess, StepNames.PromoteKey, None)]
    [InlineData(Operations.RotateAccess, StepNames.ApproveRemoval, None)]
    [InlineData(Operations.RotateAccess, StepNames.RemoveKey, None)]
    [InlineData(Operations.RotateAccess, StepNames.VerifyRemoval, None)]
    [InlineData(Operations.RotateAccess, StepNames.ConfirmRefused, "ConfirmRefusedEffect")]
    // A pipeline's approval has no stage; its other steps do, and their effects are the stage's, before any of this.
    [InlineData(Operations.Pipeline, StepNames.Approval, None)]
    // As the switch had it: an apply of anything but a provision forgets the outputs, a verify of anything but an attach is a
    // provision's verify, and a protect of anything but an attach has no effect.
    [InlineData(Unknown, StepNames.Apply, "ForgetOutputsEffect")]
    [InlineData(Unknown, StepNames.Verify, "ProvisionVerifyEffect")]
    [InlineData(Unknown, StepNames.Protect, None)]
    // A plan's and a verify-absent's effect is the same in every run; a plan's branches on the operation inside it.
    [InlineData(Unknown, StepNames.Plan, "PlanEffect")]
    [InlineData(Unknown, StepNames.VerifyAbsent, "VerifyAbsentEffect")]
    public void Each_step_of_each_operation_has_one_effect(string operation, string step, string expected) =>
        Assert.Equal(expected, RunEngine.StepEffectFor(step, operation)?.GetType().Name ?? None);

    [Fact]
    public void A_step_has_an_effect_in_a_run_only_when_its_name_has_one_for_that_operation()
    {
        // A name with two effects that applied to one operation would throw here.
        string[] operations = [Operations.Provision, Operations.Retire, Operations.Configure, Operations.Pipeline, Operations.RotateAccess, Operations.Attach, Unknown];
        Assert.All(StepTypes.All, type => Assert.All(operations, operation =>
        {
            var registered = RunEngine.StepEffectFor(type.Name);
            var effect = RunEngine.StepEffectFor(type.Name, operation);
            Assert.True(effect is null || effect.AppliesTo(operation));
            Assert.Equal(registered is not null && registered.AppliesTo(operation), effect is not null);
        }));
    }

    [Theory]
    [InlineData(StepNames.Apply, "ProvisionApplyEffect", "ForgetOutputsEffect")]
    [InlineData(StepNames.Verify, "AttachVerifyEffect", "ProvisionVerifyEffect")]
    public void A_name_whose_effect_depends_on_the_operation_has_one_effect_for_every_operation(string step, string one, string other)
    {
        string[] operations = [Operations.Provision, Operations.Retire, Operations.Configure, Operations.Pipeline, Operations.RotateAccess, Operations.Attach, Unknown];
        Assert.All(operations, operation => Assert.Contains(RunEngine.StepEffectFor(step, operation)?.GetType().Name, new[] { one, other }));
    }
}
