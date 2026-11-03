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
        StepNames.PrepareWorker => new StepAction("managed-worker", "prepare", true),
        StepNames.InstallWorker => new StepAction("managed-worker", "install", true),
        StepNames.Plan => new StepAction("opentofu", "preview", false),
        StepNames.Apply => new StepAction("opentofu", "apply", true),
        StepNames.Unprotect or StepNames.Reprotect => new StepAction("proxmox", "set-protection", true),
        StepNames.Verify => new StepAction("proxmox", "verify-guest", false),
        StepNames.VerifyAbsent => new StepAction("proxmox", "verify-absent", false),
        StepNames.Observe => new StepAction("proxmox", "observe", false),
        StepNames.GrowRoot => new StepAction("nixos-rootfs", "grow", true),
        StepNames.VerifyRoot => new StepAction("nixos-rootfs", "verify", false),
        StepNames.InstallDiskReporter => new StepAction("guest", "install-disk-reporter", true),
        StepNames.UpdateWorkerOs => new StepAction("managed-worker", "update-os", true),
        StepNames.VerifyWorkerOs => new StepAction("managed-worker", "verify-os", false),
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
                .Select(n => n == SecretNames.ProxmoxToken ? token : n)
                .Where(n => ReleasedTo(n, stepName))
                .Concat(stepName == StepNames.Apply && context.Snippets is not null ? [SecretNames.SnippetsHostKey] : [])
                .Distinct(StringComparer.Ordinal).ToList()
            : new[] { token, SecretNames.ProxmoxCa }.Where(n => n == token || context.Secrets.ContainsKey(n)).ToList();
    }

    /// <summary>
    /// The guest's private host key goes to the plan, which writes it into the snippet and keeps it
    /// in the saved plan, and the upload key to the apply, which is the only step that uploads.
    /// </summary>
    private static bool ReleasedTo(string name, string stepName) => name switch
    {
        SecretNames.GuestHostKey => stepName == StepNames.Plan,
        SecretNames.SnippetsKey => stepName == StepNames.Apply,
        _ => true,
    };

    // Credential purpose is independent of retry safety: verify-root uses guest-agent exec.
    public static string TokenFor(string stepName) => stepName is
        StepNames.Apply or StepNames.Unprotect or StepNames.Reprotect or StepNames.GrowRoot or StepNames.VerifyRoot or StepNames.PrepareWorker or StepNames.InstallWorker
        or StepNames.InstallDiskReporter
        or StepNames.UpdateWorkerOs or StepNames.VerifyWorkerOs
        ? SecretNames.ProxmoxPrivilegedToken : SecretNames.ProxmoxReadOnlyToken;

    private static string TokenFor(string stepName, RunContext context) =>
        context.Secrets.ContainsKey(SecretNames.ProxmoxSharedToken) ? SecretNames.ProxmoxSharedToken : TokenFor(stepName);

    /// <summary>Builds the assignment for a step's attempt, or for an observation when <paramref name="step"/> and <paramref name="run"/> are null.</summary>
    public async Task<Assignment> BuildAsync(Attempt attempt, Step? step, Run? run, RunContext context, Guid agentId, CancellationToken cancellationToken)
    {
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
        await ManagedWorkerSafety.ValidateDispatchAsync(db, run?.ResourceId ?? attempt.ResourceId!.Value, run, context, agentId, cancellationToken);
        var invalid = CredentialBindings.Validate(context);
        if (invalid.Count > 0)
            throw new CredentialUnavailableException($"Blocked credentials: {string.Join(", ", invalid)}.");
        var stepName = step?.Name ?? StepNames.Observe;
        var action = ActionFor(stepName);
        if (step?.Stage is not null)
            return await BuildStageAsync(attempt, step, run!, context, agentId, cancellationToken);
        if (action.Executor == "ansible")
            return await BuildAnsibleAsync(attempt, step!, run!, context, agentId, cancellationToken);
        // Check authority and artifact integrity BEFORE resolving any plaintext, even
        // for native actions or callers that bypass normal engine advancement.
        var approvedPlan = TokenFor(stepName) == SecretNames.ProxmoxPrivilegedToken
            ? await ApprovedPlanAsync(run ?? throw new AssignmentException("Privileged work requires a run."), stepName, cancellationToken) : null;
        if (stepName == StepNames.InstallDiskReporter)
        {
            if (run is null || step is null)
                throw new AssignmentException("Reporter setup requires a run.");
            if (run.Status == RunStatuses.Uncertain || step.Status == StepStatuses.Uncertain)
                throw new AssignmentException("An uncertain guest mutation cannot be replayed; inspect before recovery.");
            var reporter = context.DiskReporter ?? throw new AssignmentException("Missing reporting configuration.");
            if (reporter.InstallationMode == "managed-nixos-v1")
            {
                if (context.ManagedWorker is null || reporter.ImageSha256 is not { Length: 64 } imageHash
                    || !imageHash.All(char.IsAsciiHexDigit) || reporter.IntervalSeconds != 3600
                    || reporter.BinarySha256 is not null || reporter.BinaryUrl is not null)
                    throw new AssignmentException("Invalid frozen native reporter provenance/ownership.");
            }
            else if (reporter.InstallationMode != "fhs-download-v1" || context.ManagedWorker is not null)
                throw new AssignmentException("Unsupported reporting installation mode; managed workers have no FHS fallback.");
            if (!await db.Resources.AnyAsync(r => r.Id == run.ResourceId && r.RetiredAt == null && r.MissingSince == null, cancellationToken))
                throw new AssignmentException("The guest is missing or retired.");
            // Approval alone does not establish root/guest readiness. Do not resolve
            // even Proxmox plaintext when a direct builder caller skips these gates.
            var prerequisites = new[] { StepNames.Verify, StepNames.GrowRoot, StepNames.VerifyRoot, StepNames.WaitWorker };
            var readiness = await db.Steps.AsNoTracking().Where(s => s.RunId == run.Id && prerequisites.Contains(s.Name)).ToListAsync(cancellationToken);
            if (!readiness.Any(s => s.Name == StepNames.Verify && s.Status == StepStatuses.Succeeded)
                || context.RootDiskBytes is not null && (!readiness.Any(s => s.Name == StepNames.GrowRoot) || !readiness.Any(s => s.Name == StepNames.VerifyRoot))
                || readiness.Any(s => s.Status != StepStatuses.Succeeded && !(s.Name == StepNames.WaitWorker && s.Status == StepStatuses.Skipped))
                || context.ManagedWorker is not null && !await db.ManagedWorkers.AnyAsync(w => w.ResourceId == run.ResourceId && w.ReadyAt != null, cancellationToken))
                throw new AssignmentException("Reporter setup requires successful guest/root verification and authenticated worker readiness.");
        }
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
        // Only these server-owned keys may leave system/, and the general resolver keeps refusing them.
        var own = names.Where(n => n is SecretNames.GuestHostKey or SecretNames.SnippetsKey).ToList();
        var stored = names.Except(own).ToList();
        var (values, missing) = await store.ResolveForAttemptAsync(
            stored.ToDictionary(n => n, n => new Definitions.StoreReference(context.Secrets[n], context.SecretVersions[n]), StringComparer.Ordinal),
            stored.Where(CredentialBindings.RequiresSecret).ToHashSet(StringComparer.Ordinal),
            attempt.Id,
            agentId,
            cancellationToken);
        foreach (var name in own)
        {
            var key = context.Secrets[name];
            var version = context.SecretVersions[name];
            if (await store.GetVersionInternalAsync(key, version, cancellationToken) is not { } value)
            {
                missing.Add($"{key} v{version}");
                continue;
            }

            db.SecretResolutions.Add(new SecretResolution { Key = key, Version = version, AttemptId = attempt.Id, AgentId = agentId, ResolvedAt = clock.GetUtcNow() });
            values[name] = value;
        }
        if (missing.Count > 0)
        {
            throw new CredentialUnavailableException($"Blocked credentials: {string.Join(", ", missing)}");
        }
        if (own.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
        values.Remove(SecretNames.SnippetsHostKey, out var nodeHostKey);
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
                ["backend"] = new JsonObject
                {
                    ["type"] = tofu.Backend.Type,
                    ["config"] = new JsonObject(tofu.Backend.Config.Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))),
                    ["workspace"] = tofu.Backend.Workspace,
                },
                ["variables"] = await VariablesAsync(run, context, tofu, cancellationToken),
                ["env"] = new JsonObject(tofu.Env.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
                // Only what this step holds: the agent refuses a name it was not given a value for.
                ["secretEnv"] = new JsonObject(tofu.SecretEnv.Where(p => p.Value == SecretNames.ProxmoxToken || values.ContainsKey(p.Value))
                    .Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
                ["destroy"] = run.Operation == Operations.Retire,
            };
            if (nodeHostKey is not null)
                input["knownHosts"] = KnownHosts(nodeHostKey, context.Snippets!);
            if (run.Operation == Operations.Provision
                && (context.RootDiskBytes is not null || context.ManagedWorker is not null || context.DiskReporter is not null))
                input["retainNoChangesPlan"] = true;
            assignment.FilesTarGz = ByteString.CopyFrom(await mirror.BundleAsync(run.Commit!, cancellationToken));
            if (stepName == StepNames.Apply)
            {
                assignment.Artifacts.Add(approvedPlan!);
            }
            if (state is not null)
            {
                assignment.Artifacts.Add(state);
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
                ["waitSeconds"] = stepName == StepNames.VerifyAbsent ? 60 : options.Value.Scheduling.VerifyWaitSeconds,
                ["protection"] = stepName == StepNames.Reprotect,
            };
            if (action.Executor == "nixos-rootfs")
            {
                input["diskBytes"] = context.RootDiskBytes ?? throw new AssignmentException("Missing root disk capacity.");
                input["address"] = context.Address;
                input["workerGeneration"] = context.ManagedWorker?.Generation.ToString();
            }
            if (stepName == StepNames.InstallDiskReporter && run is not null)
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
                if (reporter.InstallationMode == "managed-nixos-v1")
                {
                    input["address"] = context.Address;
                    input["generation"] = context.ManagedWorker!.Generation.ToString();
                }
                else
                {
                    // Keep the ordinary FHS wire shape compatible with existing control workers.
                    input["reporter"]!.AsObject().Remove("installationMode");
                }
            }
        }

        if (action.Executor == "managed-worker")
        {
            var worker = context.ManagedWorker ?? throw new AssignmentException("Missing managed worker context.");
            input["address"] = context.Address;
            input["generation"] = worker.Generation.ToString();
            if (stepName is StepNames.UpdateWorkerOs or StepNames.VerifyWorkerOs)
            {
                input["update"] = System.Text.Json.JsonSerializer.SerializeToNode(
                    context.WorkerUpdate ?? throw new AssignmentException("Missing pinned OS update release."), RunContext.Json);
                input["waitSeconds"] = stepName == StepNames.UpdateWorkerOs ? 1860 : 300;
            }
            if (stepName == StepNames.InstallWorker && run is not null)
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
    private async Task<Artifact> ApprovedPlanAsync(Run run, string stepName, CancellationToken cancellationToken)
    {
        var steps = await db.Steps.AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync(cancellationToken);
        var plan = steps.SingleOrDefault(s => s.Name == StepNames.Plan);
        var approval = steps.SingleOrDefault(s => s.Name == StepNames.Approval);
        if (plan?.Status != StepStatuses.Succeeded || approval?.Status != StepStatuses.Succeeded || approval.Decision != "approved")
        {
            throw new AssignmentException("Privileged work requires a successful plan and approval for this run. Inspect and replan.");
        }
        if (RunContext.Parse(run.Context).WorkerUpdate is { } update && approval.WorkerUpdateSha256 != update.ArchiveSha256)
            throw new AssignmentException("The exact worker OS closure archive was not explicitly approved.");
        if (RunContext.Parse(run.Context).WorkerUpdate is not null
            && !ManagedWorkerSafety.UnchangedPlan(plan.Outputs is null ? null : JsonNode.Parse(plan.Outputs)))
            throw new AssignmentException("Worker OS updates require an unchanged infrastructure plan and must never recreate or reconcile a VM.");
        var runContext = RunContext.Parse(run.Context);
        if (ManagedWorkerSafety.IsWorkerRemoval(run, runContext))
        {
            // Only a deleted worker's removal destroys a worker VM, and only once it is drained.
            if (steps.SingleOrDefault(s => s.Name == StepNames.DrainWorker)?.Status != StepStatuses.Succeeded)
                throw new AssignmentException("The worker must be drained before its VM is removed.");
        }
        else if (runContext.ManagedWorker is not null
            && ManagedWorkerSafety.DestructivePlan(plan.Outputs is null ? null : JsonNode.Parse(plan.Outputs)))
            throw new AssignmentException("Managed worker deletion/replacement is not supported, even with saved-plan approval.");
        if (stepName == StepNames.Unprotect && run.Operation == Operations.Provision
            && (runContext.ManagedWorker is not null || !GuestVmPlan.RemovesGuestVm(plan.Outputs is null ? null : JsonNode.Parse(plan.Outputs), runContext)))
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
    /// What the OpenTofu provider may connect to for an upload: the host's own key, for any name,
    /// because the provider looks a node up by an address it formats itself. Any other key is
    /// refused, and the provider never adds one beside it.
    /// </summary>
    internal static string KnownHosts(string hostKey, SnippetsContext uploads)
    {
        var lines = new List<string>();
        foreach (var line in hostKey.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].StartsWith("ssh-", StringComparison.Ordinal) && !parts[0].StartsWith("ecdsa-", StringComparison.Ordinal)
                || !parts[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
                throw new CredentialUnavailableException($"Blocked credentials: the SSH host key of {uploads.Address} is not a public key line.");
            lines.Add($"* {parts[0]} {parts[1]}");
        }

        return lines.Count == 0
            ? throw new CredentialUnavailableException($"Blocked credentials: the SSH host key of {uploads.Address} is empty.")
            : string.Join("\n", lines) + "\n";
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
        if (context.Snippets is not null && variables["octopushy_snippets"] is JsonObject snippets)
        {
            var hostKey = await db.GuestHostKeys.AsNoTracking().FirstOrDefaultAsync(k => k.ResourceId == run.ResourceId, cancellationToken)
                ?? throw new AssignmentException("The guest has no host key of Octopushy's yet.");
            snippets["hostPublicKey"] = hostKey.PublicKey;
        }

        return variables;
    }
}
