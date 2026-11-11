using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Images;

namespace Octopushy.Server.Orchestration;

public sealed partial class AssignmentBuilder
{
    /// <summary>
    /// The assignment of a template's image build: executor <c>nix</c>, action <c>build</c>, with the run bundle of the build's
    /// revision and workflow release, and no secrets. See contracts/agent/v1/executors.md, <c>nix</c>.
    /// </summary>
    public async Task<Assignment> BuildImageAsync(Attempt attempt, ImageBuild build, Guid agentId, CancellationToken cancellationToken)
    {
        if (!TemplateImages.IsBuild(attempt) || build.AttemptId != attempt.Id)
            throw new AssignmentException("The attempt is not this image's build.");
        if (await db.Agents.AnyAsync(a => a.Id == agentId && (a.Disabled || a.Draining), cancellationToken))
            throw new AssignmentException("This agent is disabled or draining and cannot receive new assignments.");
        var input = new JsonObject
        {
            ["key"] = build.Key,
            ["flake"] = BuildSource.Flake,
            ["output"] = build.Output,
            ["system"] = BuildSource.System,
        };
        if (build.LockPath is { } lockPath)
            input["lock"] = lockPath;
        return new Assignment
        {
            AttemptId = attempt.Id.ToString(),
            Executor = TemplateImages.Executor,
            Action = TemplateImages.Action,
            InputJson = input.ToJsonString(),
            LeaseSeconds = (uint)options.Value.AgentChannel.LeaseSeconds,
            FilesTarGz = ByteString.CopyFrom(await bundles.ForAsync(build.RevisionCommit, build.WorkflowRepository,
                build.WorkflowRepository is null ? null : build.WorkflowCommit, $"{build.WorkflowId}@{build.WorkflowVersion}", cancellationToken)),
        };
    }
}
