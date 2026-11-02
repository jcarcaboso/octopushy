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

    /// <summary>The file in a cluster's root that holds the primary's trusted certificate.</summary>
    private const string PinnedCertificate = "octopushy-primary.pem";

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
        StepNames.InspectCluster => new StepAction("technitium", "inspect", false),
        StepNames.DetachMembers => new StepAction("technitium", "detach", true),
        StepNames.ReconcileCluster => new StepAction("technitium", "reconcile", true),
        StepNames.RefreshBlockLists => new StepAction("technitium", "refresh-block-lists", true),
        StepNames.VerifyCluster or StepNames.ObserveCluster => new StepAction("technitium", "verify", false),
        StepNames.ProbeCluster => new StepAction("technitium", "certificates", false),
        StepNames.Preview => new StepAction("ansible", "preview", false),
        StepNames.Playbook or StepNames.PlaybookApproved => new StepAction("ansible", "run", true),
        _ => throw new InvalidOperationException($"The step '{stepName}' does not run on an agent."),
    };

    /// <summary>The secret names a step's assignment carries.</summary>
    public static IReadOnlyList<string> SecretsFor(string stepName, RunContext context)
    {
        var action = ActionFor(stepName);
        if (stepName == StepNames.ProbeCluster)
            return [];
        if (action.Executor == "technitium")
            return [SecretNames.ClusterPassword];
        if (action.Executor == "ansible")
            return AnsibleSecretsFor(stepName, context);
        var token = TokenFor(stepName, context);
        return action.Executor == "opentofu"
            ? SecretEnvFor(stepName, context.OpenTofu!).Select(p => p.Value)
                .Select(n => n == SecretNames.ProxmoxToken ? token : n).Distinct(StringComparer.Ordinal).ToList()
            : new[] { token, SecretNames.ProxmoxCa }.Where(n => n == token || context.Secrets.ContainsKey(n)).ToList();
    }

    // What a plan does not receive cannot enter the saved plan, and a plan runs before anyone
    // has approved the commit it comes from.
    private static IEnumerable<KeyValuePair<string, string>> SecretEnvFor(string stepName, OpenTofuContext tofu) =>
        stepName == StepNames.Apply ? tofu.SecretEnv : tofu.SecretEnv.Where(p => !SecretNames.ApplyOnly(p.Value));

    // Credential purpose is independent of retry safety: verify-root uses guest-agent exec.
    public static string TokenFor(string stepName) => stepName is
        StepNames.Apply or StepNames.Unprotect or StepNames.Reprotect or StepNames.GrowRoot or StepNames.VerifyRoot or StepNames.PrepareWorker or StepNames.InstallWorker
        or StepNames.InstallDiskReporter
        or StepNames.UpdateWorkerOs or StepNames.VerifyWorkerOs
        or StepNames.DetachMembers or StepNames.ReconcileCluster
        ? SecretNames.ProxmoxPrivilegedToken : SecretNames.ProxmoxReadOnlyToken;

    /// <summary>The work of an observation of this context.</summary>
    public static string ObserveStep(RunContext context) =>
        context.Cluster is null ? StepNames.Observe : context.Cluster.Probe ? StepNames.ProbeCluster : StepNames.ObserveCluster;

    private static string TokenFor(string stepName, RunContext context) =>
        context.Secrets.ContainsKey(SecretNames.ProxmoxSharedToken) ? SecretNames.ProxmoxSharedToken : TokenFor(stepName);

    /// <summary>Builds the assignment for a step's attempt, or for an observation when <paramref name="step"/> and <paramref name="run"/> are null.</summary>
    public async Task<Assignment> BuildAsync(Attempt attempt, Step? step, Run? run, RunContext context, Guid agentId, CancellationToken cancellationToken)
    {
        var blocked = run is null
            ? await RepositoryTrust.BlockedReasonAsync(db, attempt.RevisionId, context.Commit, context, attempt.ResourceId, cancellationToken)
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
        var stepName = step?.Name ?? ObserveStep(context);
        var action = ActionFor(stepName);
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
        var (values, missing) = await store.ResolveForAttemptAsync(
            names.ToDictionary(n => n, n => new Definitions.StoreReference(context.Secrets[n], context.SecretVersions[n]), StringComparer.Ordinal),
            names.Where(CredentialBindings.RequiresSecret).ToHashSet(StringComparer.Ordinal),
            attempt.Id,
            agentId,
            new SecretRelease(run?.ResourceId ?? attempt.ResourceId, context.CodeDigest, context.Address),
            cancellationToken);
        if (missing.Count > 0)
        {
            throw new CredentialUnavailableException($"Blocked credentials: {string.Join(", ", missing)}");
        }
        if (context.Cluster is null)
        {
            var token = TokenFor(stepName, context);
            values[SecretNames.ProxmoxToken] = values[token];
            values.Remove(token);
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
                ["variables"] = tofu.Variables.DeepClone(),
                ["env"] = new JsonObject(tofu.Env.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
                ["secretEnv"] = new JsonObject(SecretEnvFor(stepName, tofu).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
                ["destroy"] = run.Operation == Operations.Retire,
            };
            if (context.Cluster is not null)
            {
                if (stepName == StepNames.Plan)
                    input["replace"] = new JsonArray(ClusterPlan.Replacements(
                        await StepOutputsAsync(run, StepNames.InspectCluster, cancellationToken)).Select(a => (JsonNode?)a).ToArray());
                // The provider talks to the certificate an operator trusted, and nothing else.
                var primary = context.Cluster.Members.Single(m => m.Role == Definitions.ClusterRoles.Primary);
                var pinned = (await TrustedCertificatesAsync(context, cancellationToken))[Definitions.ClusterInputs.Endpoint(primary.Address)];
                input["files"] = new JsonObject { [PinnedCertificate] = pinned.Pem };
                input["env"]!["TECHNITIUM_CACERT"] = PinnedCertificate;
            }
            if (run.Operation == Operations.Provision
                && (context.RootDiskBytes is not null || context.ManagedWorker is not null || context.DiskReporter is not null))
                input["retainNoChangesPlan"] = true;
            // Code outside what was reviewed cannot run if it never arrives.
            var reviewed = await ProtectedSecrets.AnyAsync(db, context.Secrets.Values, cancellationToken);
            if (reviewed && context.ReviewedPaths is null)
                throw new CredentialUnavailableException("This run holds a protected secret and has no reviewed code; plan it again.");
            // Besides being the only files it receives, the agent keeps OpenTofu to them: it
            // installs no module that is not listed here, and no provider the lock file does not pin.
            // ReviewedCode.Of lists the recipe's folder, then its root, then its modules.
            if (reviewed)
                input["reviewed"] = new JsonObject
                {
                    ["modules"] = new JsonArray(context.ReviewedPaths!.Skip(2).Select(p => (JsonNode?)p).ToArray()),
                    ["noModules"] = context.CallsNoModules,
                };
            assignment.FilesTarGz = ByteString.CopyFrom(reviewed
                ? await mirror.BundleAsync(run.Commit!, context.ReviewedPaths!, cancellationToken)
                : await mirror.BundleAsync(run.Commit!, cancellationToken));
            if (stepName == StepNames.Apply)
            {
                assignment.Artifacts.Add(approvedPlan!);
            }
            if (state is not null)
            {
                assignment.Artifacts.Add(state);
            }
        }
        else if (action.Executor == "technitium")
        {
            input = context.Cluster!.Input.DeepClone().AsObject();
            if (!context.Cluster.Probe)
            {
                // Pinned at dispatch: a run can wait for trust that comes after it was made.
                var certificates = await TrustedCertificatesAsync(context, cancellationToken);
                foreach (var member in input["members"]!.AsArray())
                    member!["certificateSha256"] = certificates[member["endpoint"]!.GetValue<string>()].Sha256;
            }
            if (stepName == StepNames.DetachMembers)
            {
                ArgumentNullException.ThrowIfNull(run);
                input["remove"] = await ClusterPlan.RemovalsAsync(db, run, await StepOutputsAsync(run, StepNames.Plan, cancellationToken), cancellationToken);
            }
            if (stepName == StepNames.ReconcileCluster)
            {
                ArgumentNullException.ThrowIfNull(run);
                var plan = await StepOutputsAsync(run, StepNames.Plan, cancellationToken);
                input["join"] = new JsonArray(ClusterPlan.Joins(plan).Select(g => (JsonNode?)g).ToArray());
                // What the approval named, as inspection found it, and nothing that appeared since.
                var found = await StepOutputsAsync(run, StepNames.InspectCluster, cancellationToken);
                var inspection = ClusterPlan.PrunesEntries(plan, found) ? found : null;
                input["pruneBlocked"] = ClusterPlan.Undeclared(inspection, "blocked");
                input["pruneAllowed"] = ClusterPlan.Undeclared(inspection, "allowed");
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
            if (proxmox.GuestKind is { } guestKind)
            {
                input["guestKind"] = guestKind;
            }
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

    private async Task<Dictionary<string, MemberCertificate>> TrustedCertificatesAsync(RunContext context, CancellationToken cancellationToken)
    {
        var trust = await db.ClusterTrusts.AsNoTracking().FirstOrDefaultAsync(t => t.Address == context.Address, cancellationToken)
            ?? throw new ExecutionUntrustedException($"{RepositoryTrust.WaitingForCluster}: {context.Address} is not trusted.");
        var certificates = ClusterCertificates.Parse(trust.Certificates);
        if (!context.Cluster!.Endpoints.All(certificates.ContainsKey))
            throw new ExecutionUntrustedException($"{RepositoryTrust.WaitingForCluster}: a member's certificate is not trusted yet.");
        // What the agent is told to pin to and what the provider is given are the same certificate.
        var checkedCertificates = new Dictionary<string, MemberCertificate>(certificates);
        foreach (var endpoint in context.Cluster.Endpoints)
        {
            if (ClusterCertificates.Read(certificates[endpoint].Pem) is not { } read || read.Sha256 != certificates[endpoint].Sha256.ToLowerInvariant())
                throw new ExecutionUntrustedException($"{RepositoryTrust.WaitingForCluster}: the certificate recorded for {endpoint} does not match its fingerprint. Read the certificates again and trust the cluster again on its page.");
            checkedCertificates[endpoint] = new MemberCertificate(read.Sha256, read.Pem);
        }

        return checkedCertificates;
    }

    private async Task<JsonNode?> StepOutputsAsync(Run run, string stepName, CancellationToken cancellationToken)
    {
        var outputs = await db.Steps.AsNoTracking().Where(s => s.RunId == run.Id && s.Name == stepName)
            .Select(s => s.Outputs).FirstOrDefaultAsync(cancellationToken);
        return outputs is null ? null : JsonNode.Parse(outputs);
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
            throw new AssignmentException($"Protection is lifted only for an approved plan that replaces or deletes this guest's {GuestVmPlan.Noun(runContext)}.");
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
