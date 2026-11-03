using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;

namespace Octopushy.Server.Orchestration;

public sealed partial class AssignmentBuilder
{
    /// <summary>The secrets a step's assignment carries, named as its run records them.</summary>
    public static IReadOnlyList<string> SecretsFor(Step step, RunContext context) => step.Stage is { } stage
        ? SecretsFor(step.Name, context.ForStage(stage)).Select(role => RunContext.StageRole(stage, role)).ToList()
        : SecretsFor(step.Name, context);

    /// <summary>Builds a stage's attempt, after the checks every assignment goes through.</summary>
    private async Task<Assignment> BuildStageAsync(Attempt attempt, Step step, Run run, RunContext context, Guid agentId, CancellationToken cancellationToken)
    {
        var problems = await CredentialBindings.CheckAsync(db, run, context, cancellationToken, SecretsFor(step, context));
        if (problems.Count > 0)
            throw new CredentialUnavailableException($"Blocked credentials: {string.Join(", ", problems)}.");
        var stage = context.ForStep(step);
        return stage.Ansible is not null
            ? await BuildAnsibleAsync(attempt, step, run, stage, agentId, cancellationToken)
            : await BuildOpenTofuStageAsync(attempt, step, run, stage, cancellationToken);
    }

    /// <summary>An OpenTofu stage: its own state and its inputs. It carries no secrets; the approval still decides what applies.</summary>
    private async Task<Assignment> BuildOpenTofuStageAsync(Attempt attempt, Step step, Run run, RunContext stage, CancellationToken cancellationToken)
    {
        var apply = step.Name == StepNames.Apply;
        var approved = apply ? await ApprovedStageArtifactAsync(run, step, PlanArtifact, cancellationToken) : null;
        if (!await db.Resources.AnyAsync(r => r.Id == run.ResourceId && r.RetiredAt == null && r.MissingSince == null, cancellationToken))
            throw new AssignmentException("The service is missing or retired.");
        var tofu = stage.OpenTofu ?? throw new AssignmentException("Missing OpenTofu context.");
        var state = await states.CheckOutAsync(attempt, run, stage, apply, cancellationToken);
        var assignment = new Assignment
        {
            AttemptId = attempt.Id.ToString(),
            Executor = "opentofu",
            Action = apply ? "apply" : "preview",
            LeaseSeconds = (uint)options.Value.AgentChannel.LeaseSeconds,
            FilesTarGz = ByteString.CopyFrom(await mirror.BundleAsync(run.Commit!, cancellationToken)),
            InputJson = new JsonObject
            {
                ["stack"] = tofu.Stack,
                ["root"] = tofu.Root,
                ["image"] = tofu.Image,
                ["variables"] = tofu.Variables.DeepClone(),
                ["env"] = new JsonObject(),
                ["secretEnv"] = new JsonObject(),
                ["destroy"] = false,
            }.ToJsonString(),
        };
        if (approved is not null)
            assignment.Artifacts.Add(approved);
        assignment.Artifacts.Add(state);
        return assignment;
    }

    /// <summary>
    /// The saved plan or preview of this step's stage, if it is exactly what the approval covers: the approval
    /// names the document by digest, and the document names the artifact. Any other is refused.
    /// </summary>
    private async Task<Artifact> ApprovedStageArtifactAsync(Run run, Step step, string name, CancellationToken cancellationToken)
    {
        var steps = await db.Steps.AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync(cancellationToken);
        var approval = steps.SingleOrDefault(s => s.Name == StepNames.Approval);
        if (approval?.Status != StepStatuses.Succeeded || approval.Decision != "approved" || approval.Document is not { } document)
            throw new AssignmentException("A stage requires the pipeline's approval. Run it again.");
        if (run.Status == RunStatuses.Uncertain || step.Status == StepStatuses.Uncertain)
            throw new AssignmentException("An uncertain stage cannot be replayed; inspect the guest first.");
        if (string.IsNullOrWhiteSpace(approval.PlanSha256) || PipelineDocument.Digest(document) != approval.PlanSha256)
            throw new AssignmentException("The pipeline's document is not the one that was approved.");
        var source = steps.SingleOrDefault(s => s.Stage == step.Stage && s.Name is StepNames.Plan or StepNames.Preview);
        if (source?.Status != StepStatuses.Succeeded)
            throw new AssignmentException("The stage has no successful plan or preview.");
        var sourceAttempt = await db.Attempts.AsNoTracking()
            .Where(a => a.StepId == source.Id && a.Status == AttemptStatuses.Succeeded)
            .OrderByDescending(a => a.Number)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new AssignmentException("The stage has no successful attempt to apply.");
        var record = await db.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.AttemptId == sourceAttempt.Id && a.Name == name, cancellationToken)
            ?? throw new AssignmentException("The stage has no saved plan or preview.");
        if (string.IsNullOrWhiteSpace(record.Sha256) || PipelineDocument.ArtifactOf(document, step.Stage!) != record.Sha256)
            throw new AssignmentException("The saved plan or preview is not the one that was approved.");
        if (source.Name == StepNames.Plan
            && GuestVmPlan.ForeignChange(source.Outputs is null ? null : JsonNode.Parse(source.Outputs), RunContext.Parse(run.Context).ForStep(step)) is { } foreign)
            throw new AssignmentException(foreign);
        var content = await artifacts.LoadAsync(sourceAttempt.Id, name, cancellationToken);
        if (ArtifactStore.Sha256(content) != record.Sha256)
            throw new AssignmentException("The saved plan or preview does not match its recorded checksum.");
        return new Artifact { Name = name, Content = ByteString.CopyFrom(content), Sha256 = record.Sha256 };
    }
}
