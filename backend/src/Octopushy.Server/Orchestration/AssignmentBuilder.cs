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

public sealed class RevisionUntrustedException(string message) : Exception(message);

/// <summary>Builds the assignment an agent receives for an attempt.</summary>
public sealed class AssignmentBuilder(
    OctopushyDb db,
    VariableStore store,
    RepositoryMirror mirror,
    ArtifactStore artifacts,
    TimeProvider clock,
    IOptions<OctopushyOptions> options)
{
    public const string PlanArtifact = "plan.tar.gz";

    public static StepAction ActionFor(string stepName) => stepName switch
    {
        StepNames.PrepareWorker => new StepAction("managed-worker", "prepare", true),
        StepNames.InstallWorker => new StepAction("managed-worker", "install", true),
        StepNames.Plan => new StepAction("opentofu", "preview", false),
        StepNames.Apply => new StepAction("opentofu", "apply", true),
        StepNames.Unprotect => new StepAction("proxmox", "set-protection", true),
        StepNames.Verify => new StepAction("proxmox", "verify-guest", false),
        StepNames.VerifyAbsent => new StepAction("proxmox", "verify-absent", false),
        StepNames.Observe => new StepAction("proxmox", "observe", false),
        StepNames.GrowRoot => new StepAction("nixos-rootfs", "grow", true),
        StepNames.VerifyRoot => new StepAction("nixos-rootfs", "verify", false),
        StepNames.InstallDiskReporter => new StepAction("guest", "install-disk-reporter", true),
        _ => throw new InvalidOperationException($"The step '{stepName}' does not run on an agent."),
    };

    /// <summary>The secret names a step's assignment carries.</summary>
    public static IReadOnlyList<string> SecretsFor(string stepName, RunContext context)
    {
        var action = ActionFor(stepName);
        var token = TokenFor(stepName, context);
        return action.Executor == "opentofu"
            ? context.OpenTofu!.SecretEnv.Values
                .Select(n => n == SecretNames.ProxmoxToken ? token : n).Distinct(StringComparer.Ordinal).ToList()
            : new[] { token, SecretNames.ProxmoxCa }.Where(n => n == token || context.Secrets.ContainsKey(n)).ToList();
    }

    // Credential purpose is independent of retry safety: verify-root uses guest-agent exec.
    public static string TokenFor(string stepName) => stepName is
        StepNames.Apply or StepNames.Unprotect or StepNames.GrowRoot or StepNames.VerifyRoot or StepNames.PrepareWorker or StepNames.InstallWorker
        or StepNames.InstallDiskReporter
        ? SecretNames.ProxmoxPrivilegedToken : SecretNames.ProxmoxReadOnlyToken;

    private static string TokenFor(string stepName, RunContext context) =>
        context.Secrets.ContainsKey(SecretNames.ProxmoxSharedToken) ? SecretNames.ProxmoxSharedToken : TokenFor(stepName);

    public async Task<Assignment> BuildAsync(Attempt attempt, Step step, Run run, RunContext context, Guid agentId, CancellationToken cancellationToken)
    {
        if (await RepositoryTrust.BlockedReasonAsync(db, run, cancellationToken) is { } reason)
        {
            throw new RevisionUntrustedException(reason);
        }
        await ManagedWorkerSafety.ValidateDispatchAsync(db, run, context, agentId, cancellationToken);
        var invalid = CredentialBindings.Validate(context);
        if (invalid.Count > 0)
            throw new CredentialUnavailableException($"Blocked credentials: {string.Join(", ", invalid)}.");
        if (attempt.RunId != run.Id || step.RunId != run.Id || attempt.StepId != step.Id)
            throw new AssignmentException("The attempt, step and run do not match.");
        var action = ActionFor(step.Name);
        // Check authority and artifact integrity BEFORE resolving any plaintext, even
        // for native actions or callers that bypass normal engine advancement.
        var approvedPlan = TokenFor(step.Name) == SecretNames.ProxmoxPrivilegedToken
            ? await ApprovedPlanAsync(run, cancellationToken) : null;
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
        var token = TokenFor(step.Name, context);
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
            if (run.Operation == Operations.Provision
                && (context.RootDiskBytes is not null || context.ManagedWorker is not null || context.DiskReporter is not null))
                input["retainNoChangesPlan"] = true;
            assignment.FilesTarGz = ByteString.CopyFrom(await mirror.BundleAsync(run.Commit!, cancellationToken));
            if (step.Name == StepNames.Apply)
            {
                assignment.Artifacts.Add(approvedPlan!);
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
                input["workerGeneration"] = context.ManagedWorker?.Generation.ToString();
            }
            if (step.Name == StepNames.InstallDiskReporter)
            {
                var reporter = context.DiskReporter
                    ?? throw new AssignmentException("The run has no disk reporter configuration.");
                if (!await db.Resources.AnyAsync(r => r.Id == run.ResourceId && r.RetiredAt == null && r.MissingSince == null, cancellationToken))
                {
                    throw new AssignmentException("The guest is missing or retired.");
                }
                await GuestReporterCredentials.EnsureAsync(
                    db, store, run.ResourceId, reporter.IntervalSeconds, rotate: false, cancellationToken);
                // Only this server-owned credential may leave system/. The general
                // variable resolver must keep rejecting internal keys from definitions.
                var key = GuestReporterCredentials.Key(run.ResourceId);
                var credential = await store.GetAsync(key, cancellationToken)
                    ?? throw new AssignmentException("The reporting credential is unavailable.");
                db.SecretResolutions.Add(new SecretResolution
                {
                    Key = key,
                    Version = credential.Version,
                    AttemptId = attempt.Id,
                    AgentId = agentId,
                    ResolvedAt = clock.GetUtcNow(),
                });
                await db.SaveChangesAsync(cancellationToken);
                values[SecretNames.DiskReporterToken] = credential.Value;
                input["reporter"] = System.Text.Json.JsonSerializer.SerializeToNode(reporter, RunContext.Json);
            }
        }

        if (action.Executor == "managed-worker")
        {
            var worker = context.ManagedWorker ?? throw new AssignmentException("Missing managed worker context.");
            input["address"] = context.Address;
            input["generation"] = worker.Generation.ToString();
            if (step.Name == StepNames.InstallWorker)
            {
                var binding = await db.ManagedWorkers.SingleAsync(w => w.ResourceId == run.ResourceId, cancellationToken);
                if (binding.AgentId is null || binding.CertificatePem is null || binding.CaCertificatePem is null)
                    throw new AssignmentException("Managed worker has no issued public identity.");
                input["identity"] = new JsonObject
                {
                    ["protocolVersion"] = 1,
                    ["generation"] = worker.Generation.ToString(),
                    ["agentId"] = binding.AgentId.ToString(),
                    ["name"] = context.Proxmox.Name,
                    ["serverUrl"] = binding.ServerUrl,
                    ["certificatePem"] = binding.CertificatePem,
                    ["caCertificatePem"] = binding.CaCertificatePem,
                };
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
        var plan = steps.SingleOrDefault(s => s.Name == StepNames.Plan);
        var approval = steps.SingleOrDefault(s => s.Name == StepNames.Approval);
        if (plan?.Status != StepStatuses.Succeeded || approval?.Status != StepStatuses.Succeeded || approval.Decision != "approved")
        {
            throw new AssignmentException("Privileged work requires a successful plan and approval for this run. Inspect and replan.");
        }
        if (RunContext.Parse(run.Context).ManagedWorker is not null
            && ManagedWorkerSafety.DestructivePlan(plan.Outputs is null ? null : JsonNode.Parse(plan.Outputs)))
            throw new AssignmentException("Managed worker deletion/replacement is not supported, even with saved-plan approval.");
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
}
