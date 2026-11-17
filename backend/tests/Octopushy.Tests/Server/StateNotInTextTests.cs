using Octopushy.Definitions;
using Octopushy.Server.Changes;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>
/// What the server decides on is recorded as such, never read out of the words it shows: other words decide the same, and the
/// words alone decide nothing. The superseded run and the run blocked for trust are in ChangeTests and InstallationTrustTests.
/// </summary>
public class StateNotInTextTests
{
    private const string File = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";

    [Theory]
    [InlineData(IssueSources.WorkflowSchema, "hardware/memory: must be at least 256 (workflow test-vm@1.0)", false)]
    [InlineData(IssueSources.WorkflowSchema, "Memory is below what the workflow takes.", false)]
    [InlineData(IssueSources.Definition, "The template 'fedora' is not defined.", true)]
    [InlineData(IssueSources.Definition, "Something quoted (workflow test-vm@1.0) from the file.", true)]
    public void A_workflow_schemas_issue_is_left_to_its_pointer_by_its_source_not_its_words(string source, string message, bool byLine) =>
        Assert.Equal(byLine, ChangeService.PlacedByLine(new DefinitionIssue(IssueSeverity.Error, File, 3, message, "home/korriban/krb-app-01", source), File));

    [Fact]
    public void An_issue_in_another_file_is_never_placed_on_the_form() =>
        Assert.False(ChangeService.PlacedByLine(new DefinitionIssue(IssueSeverity.Error, "other.yaml", 3, "x"), File));

    [Theory]
    [InlineData(false, "The plan has no changes.", true)]
    [InlineData(false, "Nothing to apply: the stage already matches.", true)]
    [InlineData(true, "The plan has no changes.", false)]
    [InlineData(null, "The plan has no changes.", false)]
    public void An_apply_skipped_for_a_plan_with_nothing_to_apply_is_known_by_the_plan_not_its_words(bool? hasChanges, string detail, bool noChanges)
    {
        var run = new Run { Operation = Operations.Pipeline, Reason = "manual" };
        var plan = new Step
        {
            Name = StepNames.Plan, Kind = StepKinds.Agent, Stage = "expose", Status = StepStatuses.Succeeded,
            Outputs = hasChanges is { } has ? $$"""{"hasChanges": {{(has ? "true" : "false")}}}""" : null,
        };
        var apply = new Step { Name = StepNames.Apply, Kind = StepKinds.Agent, Stage = "expose", Status = StepStatuses.Skipped, Detail = detail };
        var other = new Step { Name = StepNames.Apply, Kind = StepKinds.Agent, Stage = "deploy", Status = StepStatuses.Skipped, Detail = "An earlier step did not succeed." };
        run.Steps = [plan, apply, other];

        Assert.Equal(noChanges, RunEngine.PlannedNoChanges(run, apply));
        Assert.False(RunEngine.PlannedNoChanges(run, other));
    }

    [Fact]
    public void An_apply_that_did_not_skip_is_repeated_whatever_its_plan_said()
    {
        var plan = new Step { Name = StepNames.Plan, Kind = StepKinds.Agent, Stage = "expose", Status = StepStatuses.Succeeded, Outputs = """{"hasChanges": false}""" };
        var apply = new Step { Name = StepNames.Apply, Kind = StepKinds.Agent, Stage = "expose", Status = StepStatuses.Failed, Detail = "The plan has no changes." };
        var run = new Run { Operation = Operations.Pipeline, Reason = "manual", Steps = [plan, apply] };
        Assert.False(RunEngine.PlannedNoChanges(run, apply));
    }
}
