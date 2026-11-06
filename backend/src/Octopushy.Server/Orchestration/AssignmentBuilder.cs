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
    Agents.AttemptMasks masks,
    Agents.AttemptLogStore logs,
    RunBundles bundles,
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
                .Select(n => n == SecretNames.ProxmoxToken ? token : n)
                .Where(n => ReleasedTo(n, stepName))
                .Concat(stepName == StepNames.Apply && context.Snippets is not null ? [SecretNames.SnippetsHostKey] : [])
                .Distinct(StringComparer.Ordinal).ToList()
            : new[] { token, SecretNames.ProxmoxCa }.Where(n => n == token || context.Secrets.ContainsKey(n)).ToList();
    }

    /// <summary>
    /// The guest's private host key goes to the plan, which writes it into the snippet and keeps it in the saved plan,
    /// and the upload key to the apply, which is the only step that uploads.
    /// </summary>
    private static bool ReleasedTo(string name, string stepName) => name switch
    {
        SecretNames.GuestHostKey => stepName == StepNames.Plan,
        SecretNames.SnippetsKey => stepName == StepNames.Apply,
        _ => true,
    };

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
        // An observation of one installation may not carry another's denied token or reach its endpoint.
        if (run is null && await SafetyBoundary.ObservationRefusalAsync(db,
                await db.Resources.AsNoTracking().Where(r => r.Id == attempt.ResourceId).Select(r => r.Address).FirstOrDefaultAsync(cancellationToken),
                context, cancellationToken) is { } observation)
            throw new AssignmentException(observation);
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
        if (run is { Operation: Operations.Provision or Operations.Retire } && stepName == StepNames.Plan && context.Proxmox.Vmid is { } vmid
            && await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == run.ResourceId, cancellationToken) is { ParentAddress: { } installation } guest
            && await SafetyBoundary.VmidCollisionAsync(db, guest, installation, vmid, cancellationToken) is { } collision)
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
        // The last time the host key rule is asked before a machine may get the key; then the key counts as spent.
        if (stepName == StepNames.Apply && run is { Operation: Operations.Provision } && context.Snippets is not null)
            await SpendHostKeyAsync(run, context, cancellationToken);

        // State leaves the controller under the gates above, like any other credential.
        var state = action.Executor == "opentofu"
            ? await states.CheckOutAsync(attempt, run!, context, stepName == StepNames.Apply, cancellationToken)
            : null;
        // Only these server-owned keys may leave system/ here, and the general resolver keeps refusing them.
        var own = names.Where(n => n is SecretNames.GuestHostKey or SecretNames.SnippetsKey).ToList();
        var stored = names.Except(own).ToList();
        var (values, missing) = await store.ResolveForAttemptAsync(
            stored.ToDictionary(n => n, n => new Definitions.StoreReference(context.Secrets[n], context.SecretVersions[n]), StringComparer.Ordinal),
            stored.Where(CredentialBindings.RequiresSecret).ToHashSet(StringComparer.Ordinal),
            attempt.Id,
            agentId,
            cancellationToken);
        if (missing.Count == 0)
        {
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
        }
        if (missing.Count > 0)
        {
            foreach (var audit in db.ChangeTracker.Entries<SecretResolution>().Where(e => e.State == EntityState.Added).ToList())
                audit.State = EntityState.Detached;
            throw new CredentialUnavailableException($"Blocked credentials: {string.Join(", ", missing)}");
        }
        if (action.Executor == "opentofu" && run is { Operation: Operations.Provision or Operations.Retire })
            await AddHostKeyMasksAsync(attempt, run, values, agentId, cancellationToken);
        if (own.Count > 0 || values.Keys.Any(k => k.StartsWith(SecretNames.HostKeyMaskPrefix, StringComparison.Ordinal)))
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
                ["variables"] = await VariablesAsync(run, context, tofu, cancellationToken),
                ["env"] = new JsonObject(tofu.Env.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
                // Only what this step holds: the agent refuses a name it was not given a value for.
                ["secretEnv"] = new JsonObject(tofu.SecretEnv.Where(p => p.Value == SecretNames.ProxmoxToken || values.ContainsKey(p.Value))
                    .Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
                ["destroy"] = run.Operation == Operations.Retire,
            };
            if (nodeHostKey is not null)
                input["knownHosts"] = KnownHosts(nodeHostKey, context.Snippets!);
            assignment.FilesTarGz = ByteString.CopyFrom(await bundles.ForAsync(run, context, cancellationToken));
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
                // qemu or lxc: verify and protection go to that endpoint, and a machine of the other kind fails them.
                ["kind"] = proxmox.MachineKind,
                ["name"] = proxmox.Name,
                ["expectRunning"] = true,
                ["expectIpv4"] = proxmox.ExpectIpv4,
                ["waitSeconds"] = stepName == StepNames.VerifyAbsent ? 60 : options.Value.Scheduling.VerifyWaitSeconds,
                ["protection"] = stepName == StepNames.Reprotect,
            };
            // A container's host keys, from the create task of the apply that made it, for its Ansible work to be pinned to.
            // The request is kept with the attempt it was made for: only that attempt's answer is ever pinned.
            if (stepName == StepNames.Verify && context.ContainerHostKey && run is not null
                && await CreationAsync(run, context, cancellationToken) is { } creation)
            {
                input["hostKeys"] = creation.Request;
                run.Context = (RunContext.Parse(run.Context) with
                {
                    HostKeysRequest = new HostKeysRequestContext(attempt.Id, creation.CreatedBy, creation.Request["optional"]!.GetValue<bool>()),
                }).Serialize();
            }
        }

        if (stepName == StepNames.Apply && run!.Operation == Operations.Provision)
        {
            await ReserveAsync(run, cancellationToken);
            RecordApplyPrincipal(run, context, values);
        }
        if (stepName == StepNames.Apply && run!.Operation is Operations.Provision or Operations.Retire)
            await WithdrawContainerPinAsync(run, context, cancellationToken);
        assignment.InputJson = input.ToJsonString();
        foreach (var (name, value) in values)
        {
            assignment.Secrets[name] = value;
        }

        return assignment;
    }

    /// <summary>
    /// Records a reservation as this approved apply, whose plan creates the guest's own machine, is sent: its VMID
    /// on its node, in its installation, and the time it was first sent for that machine. It grants nothing; see
    /// <see cref="Resource.ReservedVmid"/>. The plan has to say so; a state's contents never count.
    /// </summary>
    private async Task ReserveAsync(Run run, CancellationToken cancellationToken)
    {
        var context = RunContext.Parse(run.Context);
        var plan = await db.Steps.AsNoTracking().Where(s => s.RunId == run.Id && s.Name == StepNames.Plan).Select(s => s.Outputs).FirstOrDefaultAsync(cancellationToken);
        if (plan is null || !GuestVmPlan.MakesGuestMachine(JsonNode.Parse(plan), context))
            return;
        var resource = await db.Resources.FirstAsync(r => r.Id == run.ResourceId, cancellationToken);
        if (resource.BoundVmid is not null)
            return;
        // A retry of the same create keeps the first time: what was observed since may be the VM it made.
        if (resource.ReservedAt is null || resource.ReservedInstallation != resource.ParentAddress
            || resource.ReservedNode != context.Proxmox.Node || resource.ReservedVmid != context.Proxmox.Vmid)
            (resource.ReservedAt, resource.ReservationAppliedAt) = (clock.GetUtcNow(), null);
        resource.ReservedInstallation = resource.ParentAddress;
        resource.ReservedNode = context.Proxmox.Node;
        resource.ReservedVmid = context.Proxmox.Vmid;
    }

    /// <summary>
    /// Which creation a container's host keys may come from, as the agent's <c>hostKeys</c> request: the provision run
    /// whose apply created the guest's container, by the token principal recorded when that apply was sent, and the window
    /// it ran in, counted back from now. Proxmox logs the create task's owner as that principal, and the agent finds the
    /// task in the node's own clock, so no clock but the server's is compared with anything.
    /// <list type="bullet">
    /// <item>A run whose plan created the container asks about its own apply, and must get the keys.</item>
    /// <item>Any other run asks only while the guest has no pin in use, as after a verify that failed, and about the last
    /// run that created this container. The agent checks again that nothing changed the VMID since, and a failure to vouch
    /// is reported, not fatal: nothing was created now. A pin is never revived; the keys read become a new version.</item>
    /// </list>
    /// Null when there is no such creation on record: then nothing is pinned. See docs/guest-configuration.md.
    /// </summary>
    private async Task<(JsonObject Request, Guid CreatedBy)?> CreationAsync(Run run, RunContext context, CancellationToken cancellationToken)
    {
        // What the apply was sent with is in the run's context as stored, which may be newer than the view this step was built from.
        var own = await CreatedAsync(run.Id, RunContext.Parse(run.Context), cancellationToken);
        if (own is not null)
            return (Request(own.Value, required: true), run.Id);
        if (await ContainerHostKeys.InUseAsync(db, run.ResourceId, cancellationToken))
            return null;
        var earlier = await db.Runs.AsNoTracking()
            .Where(r => r.ResourceId == run.ResourceId && r.Id != run.Id && r.Operation == Operations.Provision && r.CreatedAt <= run.CreatedAt)
            .OrderByDescending(r => r.CreatedAt).Select(r => new { r.Id, r.Context }).ToListAsync(cancellationToken);
        foreach (var candidate in earlier)
        {
            if (await CreatedAsync(candidate.Id, RunContext.Parse(candidate.Context), cancellationToken) is { } creation)
                return creation.Context.Proxmox.Vmid == context.Proxmox.Vmid && creation.Context.Proxmox.Node == context.Proxmox.Node
                    ? (Request(creation, required: false), candidate.Id)
                    : null;
        }

        return null;

        JsonObject Request((RunContext Context, DateTimeOffset Started, DateTimeOffset Finished) creation, bool required)
        {
            var now = clock.GetUtcNow();
            return new JsonObject
            {
                ["createdBy"] = creation.Context.ApplyPrincipal,
                ["applyStartedSecondsAgo"] = (long)Math.Ceiling(Math.Max(0, (now - creation.Started).TotalSeconds)),
                ["applyFinishedSecondsAgo"] = (long)Math.Floor(Math.Max(0, (now - creation.Finished).TotalSeconds)),
                ["optional"] = !required,
            };
        }
    }

    /// <summary>
    /// The run's creation of its guest's container, if it made one: its approved plan created the guest's machine, its apply
    /// succeeded, and the principal it was sent with is on record. Its context and the window its apply ran in.
    /// </summary>
    private async Task<(RunContext Context, DateTimeOffset Started, DateTimeOffset Finished)?> CreatedAsync(Guid runId, RunContext context,
        CancellationToken cancellationToken)
    {
        if (context.ApplyPrincipal is null)
            return null;
        var steps = await db.Steps.AsNoTracking().Where(s => s.RunId == runId && (s.Name == StepNames.Plan || s.Name == StepNames.Apply))
            .ToListAsync(cancellationToken);
        var plan = steps.SingleOrDefault(s => s.Name == StepNames.Plan);
        var apply = steps.SingleOrDefault(s => s.Name == StepNames.Apply);
        if (plan?.Outputs is null || apply?.Status != StepStatuses.Succeeded
            || !GuestVmPlan.MakesGuestMachine(JsonNode.Parse(plan.Outputs), context))
            return null;
        var attempt = await db.Attempts.AsNoTracking()
            .Where(a => a.StepId == apply.Id && a.Status == AttemptStatuses.Succeeded)
            .OrderByDescending(a => a.Number).FirstOrDefaultAsync(cancellationToken);
        return attempt?.FinishedAt is { } finished && (attempt.AssignedAt ?? attempt.StartedAt) is { } started
            ? (context, started, finished)
            : null;
    }

    /// <summary>
    /// The principal a container's provision apply is sent with, kept in the run's context: Proxmox records it as the owner of
    /// the create task, and only the token's id goes there, never its secret. Kept so the creation can be told later, even
    /// once that token version is revoked.
    /// </summary>
    private static void RecordApplyPrincipal(Run run, RunContext context, Dictionary<string, string> values)
    {
        if (context.Proxmox.MachineKind != "lxc" || !values.TryGetValue(SecretNames.ProxmoxToken, out var token) || token.IndexOf('=') is not (> 0 and var equals))
            return;
        run.Context = (RunContext.Parse(run.Context) with { ApplyPrincipal = token[..equals] }).Serialize();
    }

    /// <summary>
    /// Before an apply that creates, replaces or deletes the guest's container is sent, its pin stops being usable: once the
    /// apply may have run, the machine at the VMID may not be the one the pin was read from, whatever the apply then
    /// reports. Setup, services and rotations wait until a verify of the new container pins it again. See
    /// <see cref="ContainerHostKeys.WithdrawAsync"/>.
    /// </summary>
    private async Task WithdrawContainerPinAsync(Run run, RunContext context, CancellationToken cancellationToken)
    {
        if (context.Proxmox.MachineKind != "lxc")
            return;
        var plan = await db.Steps.AsNoTracking().Where(s => s.RunId == run.Id && s.Name == StepNames.Plan).Select(s => s.Outputs).FirstOrDefaultAsync(cancellationToken);
        var outputs = plan is null ? null : JsonNode.Parse(plan);
        if (GuestVmPlan.MakesGuestMachine(outputs, context) || GuestVmPlan.RemovesGuestVm(outputs, context) || GuestVmPlan.MakesUnnamedMachine(outputs))
            await ContainerHostKeys.WithdrawAsync(db, run.ResourceId, clock.GetUtcNow(),
                $"Run {run.Id} applies a plan that may replace or remove container {context.Proxmox.Vmid}: this pin was read from the one before.", cancellationToken);
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
            throw new AssignmentException(await OutputDiagnostics.MaskAsync(db, masks, logs, plan, foreign, cancellationToken));
        if (stepName == StepNames.Unprotect && run.Operation is Operations.Provision or Operations.Retire
            && !GuestVmPlan.RemovesGuestVm(plan.Outputs is null ? null : JsonNode.Parse(plan.Outputs), runContext))
            throw new AssignmentException($"Protection is lifted only for an approved plan that replaces or deletes this guest's {runContext.Proxmox.Machine}.");
        if (stepName == StepNames.Unprotect
            && (await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == run.ResourceId, cancellationToken) is not { ParentAddress: { } installation } guest
                || runContext.Proxmox.Vmid is not { } vmid
                || !await SafetyBoundary.OwnsAsync(db, guest, installation, runContext.Proxmox.Node, vmid, cancellationToken)))
            throw new AssignmentException($"Protection is lifted only from a machine this guest owns, and {runContext.Proxmox.Machine} {runContext.Proxmox.Vmid} on {runContext.Proxmox.Node} is not shown to be its own.");
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
        // The public half of the version this run pinned, which is the private half its plan holds.
        if (context.Snippets is not null && variables["octopushy_snippets"] is JsonObject snippets)
        {
            var version = context.SecretVersions.GetValueOrDefault(SecretNames.GuestHostKey);
            var hostKey = await db.GuestHostKeys.AsNoTracking().FirstOrDefaultAsync(k => k.ResourceId == run.ResourceId && k.Version == version, cancellationToken)
                ?? throw new AssignmentException($"The guest has no host key of Octopushy's at version {version}.");
            snippets["hostPublicKey"] = hostKey.PublicKey;
        }

        return variables;
    }

    /// <summary>
    /// Refuses an apply whose plan's host key no longer fits the machine it makes, whatever path led here, and otherwise
    /// marks the key as spent: from the moment this apply is handed out, a machine may hold it. The mark is saved with the
    /// hand-off, before the assignment is sent. A mark this run made itself, by an earlier hand-off, is its own.
    /// </summary>
    private async Task SpendHostKeyAsync(Run run, RunContext context, CancellationToken cancellationToken)
    {
        var version = context.SecretVersions.GetValueOrDefault(SecretNames.GuestHostKey);
        var plan = await db.Steps.AsNoTracking().Where(s => s.RunId == run.Id && s.Name == StepNames.Plan && s.Stage == null)
            .Select(s => s.Outputs).FirstOrDefaultAsync(cancellationToken);
        var outputs = plan is null ? null : JsonNode.Parse(plan);
        var keys = await db.GuestHostKeys.AsNoTracking().Where(k => k.ResourceId == run.ResourceId).ToListAsync(cancellationToken);
        var makes = GuestVmPlan.MakesGuestMachine(outputs, context) || GuestVmPlan.MakesUnnamedMachine(outputs);
        if (GuestHostCredentials.Fit(keys, version, makes, run.Id).Fit != HostKeyFit.Fits)
            throw new AssignmentException("The host key this plan carries no longer fits: it may be on another machine, or this machine was never given it. Plan again.");
        await GuestHostCredentials.SpentAsync(db, run.ResourceId, version, run.Id, clock.GetUtcNow(), cancellationToken);
    }

    /// <summary>
    /// Every version of the guest's private host key, for the agent and the controller to mask, never for the provider. An
    /// OpenTofu step of the guest holds them whether or not it was given one: the saved plan holds the snippet the plan wrote,
    /// and the state the snippet an earlier apply wrote, with an older version. A step that already holds one under its own
    /// role is not given it twice. Each is recorded as released to the attempt.
    /// </summary>
    private async Task AddHostKeyMasksAsync(Attempt attempt, Run run, Dictionary<string, string> values, Guid agentId, CancellationToken cancellationToken)
    {
        var key = GuestHostCredentials.Key(run.ResourceId);
        var versions = await db.StoreVersions.AsNoTracking().Where(v => v.Key == key && v.RevokedAt == null)
            .Select(v => v.Version).OrderBy(v => v).ToListAsync(cancellationToken);
        foreach (var version in versions)
        {
            if (await store.GetVersionInternalAsync(key, version, cancellationToken) is not { } value || values.ContainsValue(value))
                continue;
            values[SecretNames.HostKeyMask(version)] = value;
            db.SecretResolutions.Add(new SecretResolution { Key = key, Version = version, AttemptId = attempt.Id, AgentId = agentId, ResolvedAt = clock.GetUtcNow() });
        }
    }

    /// <summary>
    /// What the OpenTofu provider may connect to for an upload: the host's own key, for any name, because the provider
    /// looks a node up by an address it formats itself. Any other key is refused, and the provider never adds one beside it.
    /// </summary>
    internal static string KnownHosts(string hostKey, SnippetsContext uploads)
    {
        var lines = new List<string>();
        foreach (var line in hostKey.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].StartsWith("ssh-", StringComparison.Ordinal) && !parts[0].StartsWith("ecdsa-", StringComparison.Ordinal)
                || !parts[0].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '@')
                || !parts[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
                throw new CredentialUnavailableException($"Blocked credentials: the SSH host key of {uploads.Address} is not a public key line.");
            lines.Add($"* {parts[0]} {parts[1]}");
        }

        return lines.Count == 0
            ? throw new CredentialUnavailableException($"Blocked credentials: the SSH host key of {uploads.Address} is empty.")
            : string.Join("\n", lines) + "\n";
    }
}
