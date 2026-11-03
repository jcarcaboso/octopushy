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

public sealed class ExecutionUntrustedException(string message) : Exception(message);

/// <summary>Builds the assignment an agent receives for an attempt.</summary>
public sealed partial class AssignmentBuilder(
    OctopushyDb db,
    VariableStore store,
    RepositoryMirror mirror,
    ArtifactStore artifacts,
    TofuStateStore states,
    TimeProvider clock,
    IOptions<OctopushyOptions> options)
{
    public const string PlanArtifact = "plan.tar.gz";

    public static StepAction ActionFor(string stepName) => stepName switch
    {
        StepNames.Plan => new StepAction("opentofu", "preview", false),
        StepNames.Apply => new StepAction("opentofu", "apply", true),
        StepNames.Unprotect or StepNames.Reprotect => new StepAction("proxmox", "set-protection", true),
        StepNames.Verify => new StepAction("proxmox", "verify-guest", false),
        StepNames.VerifyAbsent => new StepAction("proxmox", "verify-absent", false),
        StepNames.Observe => new StepAction("proxmox", "observe", false),
        StepNames.Preview => new StepAction("ansible", "preview", false),
        StepNames.Playbook or StepNames.PlaybookApproved or StepNames.AuthorizeKey or StepNames.RemoveKey => new StepAction("ansible", "run", true),
        StepNames.VerifyCurrent or StepNames.VerifyKey or StepNames.VerifyRemoval or StepNames.ConfirmRefused => new StepAction("ansible", "run", false),
        _ => throw new InvalidOperationException($"The step '{stepName}' does not run on an agent."),
    };

    /// <summary>The secret names a step's assignment carries.</summary>
    public static IReadOnlyList<string> SecretsFor(string stepName, RunContext context)
    {
        var action = ActionFor(stepName);
        if (action.Executor == "ansible")
            return AnsibleSecretsFor(stepName, context);
        var token = TokenFor(stepName, context);
        return action.Executor == "opentofu"
            ? context.OpenTofu!.SecretEnv.Values
                .Select(n => n == SecretNames.ProxmoxToken ? token : n).Distinct(StringComparer.Ordinal).ToList()
            : new[] { token, SecretNames.ProxmoxCa }.Where(n => n == token || context.Secrets.ContainsKey(n)).ToList();
    }

    public static string TokenFor(string stepName) => stepName is
        StepNames.Apply or StepNames.Unprotect or StepNames.Reprotect
        ? SecretNames.ProxmoxPrivilegedToken : SecretNames.ProxmoxReadOnlyToken;

    private static string TokenFor(string stepName, RunContext context) =>
        context.Secrets.ContainsKey(SecretNames.ProxmoxSharedToken) ? SecretNames.ProxmoxSharedToken : TokenFor(stepName);

    /// <summary>Builds the assignment for a step's attempt, or for an observation when <paramref name="step"/> and <paramref name="run"/> are null.</summary>
    public async Task<Assignment> BuildAsync(Attempt attempt, Step? step, Run? run, RunContext context, Guid agentId, CancellationToken cancellationToken)
    {
        // Only an observation reaches an installation Octopushy only observes. Asked first, so this fails and never waits.
        if (run is not null && await SafetyBoundary.ObservedOnlyRefusalAsync(db, run, context, cancellationToken) is { } observedOnly)
            throw new AssignmentException(observedOnly);
        var blocked = run is null
            ? await RepositoryTrust.BlockedReasonAsync(db, attempt.RevisionId, context.Commit, context, cancellationToken)
            : await RepositoryTrust.BlockedReasonAsync(db, run, cancellationToken);
        if (blocked is { } reason)
        {
            throw new ExecutionUntrustedException(reason);
        }
        if (step is null || run is null
                ? step is not null || run is not null || attempt.StepId is not null || attempt.RunId is not null || attempt.ResourceId is null
                : attempt.RunId != run.Id || step.RunId != run.Id || attempt.StepId != step.Id)
            throw new AssignmentException("The attempt, step and run do not match.");
        if (await db.Agents.AnyAsync(a => a.Id == agentId && (a.Disabled || a.Draining), cancellationToken))
            throw new AssignmentException("This agent is disabled or draining and cannot receive new assignments.");
        var invalid = CredentialBindings.Validate(context);
        if (invalid.Count > 0)
            throw new CredentialUnavailableException($"Blocked credentials: {string.Join(", ", invalid)}.");
        var stepName = step?.Name ?? StepNames.Observe;
        var action = ActionFor(stepName);
        if (step?.Stage is not null)
            return await BuildStageAsync(attempt, step, run!, context, agentId, cancellationToken);
        if (action.Executor == "ansible")
            return await BuildAnsibleAsync(attempt, step!, run!, context, agentId, cancellationToken);
        // Observations are runtime data, so what was valid when the run was made is asked again here.
        if (run is { Operation: Operations.Provision } && stepName == StepNames.Plan && context.Proxmox.Vmid is { } vmid
            && await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == run.ResourceId, cancellationToken) is { ParentAddress: { } installation } guest
            && await SafetyBoundary.VmidCollisionAsync(db, states, guest, installation, vmid, cancellationToken, run.Id) is { } collision)
            throw new AssignmentException(collision);
        // Check authority and artifact integrity BEFORE resolving any plaintext, even
        // for native actions or callers that bypass normal engine advancement.
        var approvedPlan = TokenFor(stepName) == SecretNames.ProxmoxPrivilegedToken
            ? await ApprovedPlanAsync(run ?? throw new AssignmentException("Privileged work requires a run."), stepName, cancellationToken) : null;
        var names = SecretsFor(stepName, context);
        var problems = await CredentialBindings.CheckAsync(db, run, context, cancellationToken, names);
        if (problems.Count > 0)
        {
            throw new CredentialUnavailableException($"Blocked credentials: {string.Join(", ", problems)}.");
        }

        // State leaves the controller under the gates above, like any other credential.
        var state = action.Executor == "opentofu"
            ? await states.CheckOutAsync(attempt, run!, context, stepName == StepNames.Apply, cancellationToken)
            : null;
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
        var token = TokenFor(stepName, context);
        values[SecretNames.ProxmoxToken] = values[token];
        values.Remove(token);

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
            ArgumentNullException.ThrowIfNull(run);
            var tofu = context.OpenTofu!;
            input = new JsonObject
            {
                ["stack"] = tofu.Stack,
                ["root"] = tofu.Root,
                ["image"] = tofu.Image,
                ["variables"] = await VariablesAsync(run, context, tofu, cancellationToken),
                ["env"] = new JsonObject(tofu.Env.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
                ["secretEnv"] = new JsonObject(tofu.SecretEnv.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
                ["destroy"] = run.Operation == Operations.Retire,
            };
            assignment.FilesTarGz = ByteString.CopyFrom(await mirror.BundleAsync(run.Commit!, cancellationToken));
            if (stepName == StepNames.Apply)
            {
                assignment.Artifacts.Add(approvedPlan!);
            }
            assignment.Artifacts.Add(state!);
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
                ["waitSeconds"] = stepName == StepNames.VerifyAbsent ? 60 : options.Value.Scheduling.VerifyWaitSeconds,
                ["protection"] = stepName == StepNames.Reprotect,
            };
        }

        assignment.InputJson = input.ToJsonString();
        foreach (var (name, value) in values)
        {
            assignment.Secrets[name] = value;
        }

        return assignment;
    }

    /// <summary>The plan the run's approval names; any other plan is refused.</summary>
    private async Task<Artifact> ApprovedPlanAsync(Run run, string stepName, CancellationToken cancellationToken)
    {
        var steps = await db.Steps.AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync(cancellationToken);
        var plan = steps.SingleOrDefault(s => s.Name == StepNames.Plan);
        var approval = steps.SingleOrDefault(s => s.Name == StepNames.Approval);
        if (plan?.Status != StepStatuses.Succeeded || approval?.Status != StepStatuses.Succeeded || approval.Decision != "approved")
        {
            throw new AssignmentException("Privileged work requires a successful plan and approval for this run. Inspect and replan.");
        }
        var runContext = RunContext.Parse(run.Context);
        if (GuestVmPlan.ForeignChange(plan.Outputs is null ? null : JsonNode.Parse(plan.Outputs), runContext) is { } foreign)
            throw new AssignmentException(foreign);
        if (stepName == StepNames.Unprotect && run.Operation == Operations.Provision
            && !GuestVmPlan.RemovesGuestVm(plan.Outputs is null ? null : JsonNode.Parse(plan.Outputs), runContext))
            throw new AssignmentException("Protection is lifted only for an approved plan that replaces or deletes this guest's VM.");
        var planAttempt = await db.Attempts.AsNoTracking()
            .Where(a => a.StepId == plan.Id && a.Status == AttemptStatuses.Succeeded)
            .OrderByDescending(a => a.Number)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new AssignmentException("The plan step has no successful attempt.");
        var record = await db.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.AttemptId == planAttempt.Id && a.Name == PlanArtifact, cancellationToken)
            ?? throw new AssignmentException("The plan has no saved plan file.");
        var planDigest = plan.Outputs is null ? null : JsonNode.Parse(plan.Outputs)?["planSha256"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(record.Sha256) || approval.PlanSha256 != record.Sha256 || planDigest != record.Sha256)
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

    /// <summary>
    /// A provision run's variables, with Octopushy's public key among the guest's. It joins them here
    /// and not in the definition, so it is never in Git. A plan and its apply get the same one.
    /// </summary>
    private async Task<JsonObject> VariablesAsync(Run run, RunContext context, OpenTofuContext tofu, CancellationToken cancellationToken)
    {
        var variables = tofu.Variables.DeepClone().AsObject();
        if (context.ManagedAccess && run.Operation == Operations.Provision)
        {
            var key = await GuestAccessCredentials.EnsureAsync(db, store, run.ResourceId, context.Proxmox.Name ?? run.ResourceId.ToString(), clock.GetUtcNow(), cancellationToken);
            // A machine that does not exist yet starts with the key in use. One that does keeps what it
            // was created with: a changed key here would reboot a VM and replace a container.
            if (!await db.Resources.AnyAsync(r => r.Id == run.ResourceId && r.BoundVmid != null, cancellationToken)
                && await GuestAccessCredentials.RebaseBootstrapAsync(db, run.ResourceId, cancellationToken))
                key = await GuestAccessCredentials.EnsureAsync(db, store, run.ResourceId, context.Proxmox.Name ?? run.ResourceId.ToString(), clock.GetUtcNow(), cancellationToken);
            variables["guest"]!["sshAuthorizedKeys"]!.AsArray().Add(key.PublicKey);
        }

        return variables;
    }
}
