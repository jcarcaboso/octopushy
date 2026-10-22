using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Options;
using Octopushy.Server.Store;

namespace Octopushy.Server.Orchestration;

/// <summary>The executor and action a step runs as, and whether that action changes infrastructure.</summary>
public sealed record StepAction(string Executor, string Action, bool Mutating);

public sealed class AssignmentException(string message) : Exception(message);

public sealed class CredentialUnavailableException(string message) : Exception(message);

/// <summary>Builds the assignment an agent receives for an attempt.</summary>
public sealed class AssignmentBuilder(
    OctopushyDb db,
    VariableStore store,
    RepositoryMirror mirror,
    ArtifactStore artifacts,
    IOptions<OctopushyOptions> options)
{
    public const string PlanArtifact = "plan.tar.gz";

    public static StepAction ActionFor(string stepName) => stepName switch
    {
        StepNames.Plan => new StepAction("opentofu", "preview", false),
        StepNames.Apply => new StepAction("opentofu", "apply", true),
        StepNames.Unprotect => new StepAction("proxmox", "set-protection", true),
        StepNames.Verify => new StepAction("proxmox", "verify-guest", false),
        StepNames.VerifyAbsent => new StepAction("proxmox", "verify-absent", false),
        StepNames.Observe => new StepAction("proxmox", "observe", false),
        StepNames.GrowRoot => new StepAction("nixos-rootfs", "grow", true),
        StepNames.VerifyRoot => new StepAction("nixos-rootfs", "verify", false),
        _ => throw new InvalidOperationException($"The step '{stepName}' does not run on an agent."),
    };

    /// <summary>The secret names a step's assignment carries.</summary>
    public static IReadOnlyList<string> SecretsFor(string stepName, RunContext context)
    {
        var action = ActionFor(stepName);
        return action.Executor == "opentofu"
            ? context.OpenTofu!.SecretEnv.Values.Distinct(StringComparer.Ordinal).ToList()
            : context.Secrets.Keys.Where(k => k is SecretNames.ProxmoxToken or SecretNames.ProxmoxCa).ToList();
    }

    public async Task<Assignment> BuildAsync(Attempt attempt, Step step, Run run, RunContext context, Guid agentId, CancellationToken cancellationToken)
    {
        var action = ActionFor(step.Name);
        var names = SecretsFor(step.Name, context);
        var problems = await CredentialBindings.CheckAsync(db, run, context, cancellationToken, names);
        if (problems.Count > 0)
        {
            throw new CredentialUnavailableException($"Blocked credentials: {string.Join(", ", problems)}.");
        }

        var (values, missing) = await store.ResolveForAttemptAsync(
            names.ToDictionary(n => n, n => new Definitions.StoreReference(context.Secrets[n], context.SecretVersions[n]), StringComparer.Ordinal),
            names.Where(CredentialBindings.RequiresSecret).ToHashSet(StringComparer.Ordinal),
            attempt.Id,
            agentId,
            cancellationToken);
        if (missing.Count > 0)
        {
            throw new CredentialUnavailableException($"Blocked credentials: {string.Join(", ", missing)}");
        }

        var assignment = new Assignment
        {
            AttemptId = attempt.Id.ToString(),
            Executor = action.Executor,
            Action = action.Action,
            LeaseSeconds = (uint)options.Value.AgentChannel.LeaseSeconds,
        };

        JsonObject input;
        if (action.Executor == "opentofu")
        {
            var tofu = context.OpenTofu!;
            input = new JsonObject
            {
                ["stack"] = tofu.Stack,
                ["root"] = tofu.Root,
                ["image"] = tofu.Image,
                ["backend"] = new JsonObject
                {
                    ["type"] = tofu.Backend.Type,
                    ["config"] = new JsonObject(tofu.Backend.Config.Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))),
                    ["workspace"] = tofu.Backend.Workspace,
                },
                ["variables"] = tofu.Variables.DeepClone(),
                ["env"] = new JsonObject(tofu.Env.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
                ["secretEnv"] = new JsonObject(tofu.SecretEnv.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
                ["destroy"] = run.Operation == Operations.Retire,
            };
            assignment.FilesTarGz = ByteString.CopyFrom(await mirror.BundleAsync(run.Commit!, cancellationToken));
            if (step.Name == StepNames.Apply)
            {
                assignment.Artifacts.Add(await ApprovedPlanAsync(run, cancellationToken));
            }
        }
        else
        {
            var proxmox = context.Proxmox;
            values.Remove(SecretNames.ProxmoxCa, out var caPem);
            input = new JsonObject
            {
                ["connection"] = new JsonObject
                {
                    ["endpoint"] = proxmox.Endpoint,
                    ["verifyTls"] = proxmox.VerifyTls,
                    ["caCertificatePem"] = caPem,
                },
                ["node"] = proxmox.Node,
                ["vmid"] = proxmox.Vmid,
                ["name"] = proxmox.Name,
                ["expectRunning"] = true,
                ["expectIpv4"] = proxmox.ExpectIpv4,
                ["waitSeconds"] = step.Name == StepNames.VerifyAbsent ? 60 : options.Value.Scheduling.VerifyWaitSeconds,
                ["protection"] = false,
            };
            if (action.Executor == "nixos-rootfs")
            {
                input["diskBytes"] = context.RootDiskBytes ?? throw new AssignmentException("Missing root disk capacity.");
                input["address"] = context.Address;
            }
        }

        assignment.InputJson = input.ToJsonString();
        foreach (var (name, value) in values)
        {
            assignment.Secrets[name] = value;
        }

        return assignment;
    }

    /// <summary>The plan the run's approval names; any other plan is refused.</summary>
    private async Task<Artifact> ApprovedPlanAsync(Run run, CancellationToken cancellationToken)
    {
        var steps = await db.Steps.AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync(cancellationToken);
        var plan = steps.Single(s => s.Name == StepNames.Plan);
        var approval = steps.Single(s => s.Name == StepNames.Approval);
        var planAttempt = await db.Attempts.AsNoTracking()
            .Where(a => a.StepId == plan.Id && a.Status == AttemptStatuses.Succeeded)
            .OrderByDescending(a => a.Number)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new AssignmentException("The plan step has no successful attempt.");
        var record = await db.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.AttemptId == planAttempt.Id && a.Name == PlanArtifact, cancellationToken)
            ?? throw new AssignmentException("The plan has no saved plan file.");
        if (approval.Decision != "approved" || approval.PlanSha256 != record.Sha256)
        {
            throw new AssignmentException("The saved plan is not the one that was approved.");
        }

        var content = await artifacts.LoadAsync(planAttempt.Id, PlanArtifact, cancellationToken);
        if (ArtifactStore.Sha256(content) != record.Sha256)
        {
            throw new AssignmentException("The saved plan does not match its recorded checksum.");
        }

        return new Artifact { Name = PlanArtifact, Content = ByteString.CopyFrom(content), Sha256 = record.Sha256 };
    }
}
