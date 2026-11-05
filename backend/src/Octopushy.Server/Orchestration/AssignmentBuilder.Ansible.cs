using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Store;

namespace Octopushy.Server.Orchestration;

public sealed partial class AssignmentBuilder
{
    public const string PreviewArtifact = "preview.json";

    /// <summary>What a run that would pin Octopushy's host key is told before any apply delivered one.</summary>
    public const string GuestHostKeyNotDelivered = "Blocked credentials: this guest's host key is Octopushy's, and no provision run has "
        + "delivered it yet. Provision the guest first.";

    /// <summary>
    /// A preview gets only the pin, which is public: it runs repository code that nobody has
    /// approved. The rest goes to the steps after approval.
    /// </summary>
    private static List<string> AnsibleSecretsFor(string stepName, RunContext context) =>
        stepName != StepNames.Preview ? context.Secrets.Keys.ToList()
        : context.ManagedHostKey ? [] : [SecretNames.AnsibleHostKey];

    /// <summary>Builds a configure run's attempt, after the checks every assignment goes through.</summary>
    private async Task<Assignment> BuildAnsibleAsync(Attempt attempt, Step step, Run run, RunContext context, Guid agentId, CancellationToken cancellationToken)
    {
        var ansible = context.Ansible ?? throw new AssignmentException("Missing configure context.");
        // Check authority and artifact integrity BEFORE resolving any plaintext.
        var outcome = step.Name == StepNames.PlaybookApproved ? await ApprovedOutcomeAsync(run, cancellationToken) : null;
        var preview = step.Name == StepNames.Preview ? null
            : step.Stage is null ? await ApprovedPreviewAsync(run, step, cancellationToken)
            : await ApprovedStageArtifactAsync(run, step, PreviewArtifact, cancellationToken);
        if (!await db.Resources.AnyAsync(r => r.Id == run.ResourceId && r.RetiredAt == null && r.MissingSince == null, cancellationToken))
            throw new AssignmentException("The guest is missing or retired.");
        var snapshot = ansible.UsesAppliedSnapshot && step.Name == StepNames.Preview
            ? await db.GuestConfigurations.AsNoTracking().Where(c => c.ResourceId == run.ResourceId).Select(c => c.Snapshot).FirstOrDefaultAsync(cancellationToken)
                ?? throw new AssignmentException("This action reuses what the last successful run prepared, and the guest has none yet.")
            : null;

        var names = SecretsFor(step.Name, context);
        // A stage's are checked with the whole run, which is what pins them.
        var problems = step.Stage is null ? await CredentialBindings.CheckAsync(db, run, context, cancellationToken, names) : [];
        if (problems.Count > 0)
            throw new CredentialUnavailableException($"Blocked credentials: {string.Join(", ", problems)}.");
        // Only this server-owned key may leave system/, and the general resolver keeps refusing it.
        var rotation = context.AccessRotation;
        // A pipeline run's resource is the service: the key is its guest's.
        var guestId = context.GuestResourceId ?? run.ResourceId;
        var ownKey = names.Contains(SecretNames.AnsibleSshKey) && context.Secrets[SecretNames.AnsibleSshKey] == GuestAccessCredentials.Key(guestId);
        // Checked again here, before anything is resolved, for a run started before the record changed or by a path
        // that did not check: the private half never goes out for a login nothing shows can work.
        if ((ownKey || (rotation is not null && step.Name != StepNames.Preview))
            && !await GuestAccessCredentials.DeliveredAsync(db, guestId, cancellationToken))
            throw new AssignmentException(GuestWork.OwnKeyNotDelivered);
        // A container's own pin is the other: public, and the guest's alone.
        var ownPin = names.Contains(SecretNames.AnsibleHostKey) && context.Secrets[SecretNames.AnsibleHostKey] == ContainerHostKeys.Key(guestId);
        // One host key source per run, as RunFactory.BindHostKey chose it: never a container's pin and a managed key at once.
        if (context.ManagedHostKey && context.Secrets.Any(s => RunContext.BareRole(s.Key) == SecretNames.AnsibleHostKey))
            throw new AssignmentException("This run names two host key sources, a store pin and Octopushy's managed key. Inspect and replan.");
        var stored = names.Where(n => !(ownKey && n == SecretNames.AnsibleSshKey) && !(ownPin && n == SecretNames.AnsibleHostKey)).ToList();
        var (values, missing) = await store.ResolveForAttemptAsync(
            stored.ToDictionary(n => n, n => new Definitions.StoreReference(context.Secrets[n], context.SecretVersions[n]), StringComparer.Ordinal),
            stored.Where(CredentialBindings.RequiresSecret).ToHashSet(StringComparer.Ordinal),
            attempt.Id,
            agentId,
            cancellationToken);
        if (ownKey || (rotation is not null && step.Name != StepNames.Preview))
        {
            var key = GuestAccessCredentials.Key(guestId);
            async Task<string> ReleaseAsync(IEnumerable<int> versions)
            {
                var released = new List<string>();
                foreach (var version in versions)
                {
                    var value = await store.GetVersionInternalAsync(key, version, cancellationToken, rotation is not null);
                    if (value is null)
                    {
                        missing.Add($"{key} v{version}");
                        continue;
                    }

                    db.SecretResolutions.Add(new SecretResolution { Key = key, Version = version, AttemptId = attempt.Id, AgentId = agentId, ResolvedAt = clock.GetUtcNow() });
                    released.Add(value);
                }

                return string.Join("\n", released);
            }

            IReadOnlyList<int> inUse = rotation is null ? [context.SecretVersions[SecretNames.AnsibleSshKey]]
                : step.Name is StepNames.VerifyCurrent or StepNames.AuthorizeKey ? [rotation.Current]
                : [rotation.Candidate];
            values[SecretNames.AnsibleSshKey] = await ReleaseAsync(inUse);
            if (step.Name == StepNames.ConfirmRefused)
                values[SecretNames.AnsibleRefusedKeys] = await ReleaseAsync(rotation!.Remove);
            await db.SaveChangesAsync(cancellationToken);
        }

        if (ownPin)
        {
            var key = ContainerHostKeys.Key(guestId);
            var version = context.SecretVersions[SecretNames.AnsibleHostKey];
            if (await store.GetVersionInternalAsync(key, version, cancellationToken) is { } pin)
            {
                values[SecretNames.AnsibleHostKey] = pin;
                db.SecretResolutions.Add(new SecretResolution { Key = key, Version = version, AttemptId = attempt.Id, AgentId = agentId, ResolvedAt = clock.GetUtcNow() });
                await db.SaveChangesAsync(cancellationToken);
            }
            else
                missing.Add($"{key} v{version}");
        }

        if (missing.Count > 0)
            throw new CredentialUnavailableException($"Blocked credentials: {string.Join(", ", missing)}");
        values.Remove(SecretNames.AnsibleHostKey, out var hostKey);
        if (context.ManagedHostKey)
        {
            // Octopushy's own key, the version an apply delivered last: before any apply carried one, the guest still has
            // whatever key its image made, and nothing here connects to that.
            hostKey = (await GuestHostCredentials.PinnedAsync(db, guestId, cancellationToken))?.PublicKey
                ?? throw new CredentialUnavailableException(GuestHostKeyNotDelivered);
        }
        else if (string.IsNullOrWhiteSpace(hostKey))
            throw new CredentialUnavailableException($"Blocked credentials: {context.Secrets[SecretNames.AnsibleHostKey]}: empty");

        var input = new JsonObject
        {
            ["image"] = ansible.Image,
            ["root"] = ansible.Root,
            ["action"] = ansible.Action,
            ["playbook"] = rotation is null ? ansible.Playbook : GuestAccessPlaybooks.For(step.Name),
            ["prepare"] = ansible.Prepare,
            ["host"] = new JsonObject
            {
                ["name"] = ansible.GuestName,
                ["address"] = ansible.Address,
                ["port"] = ansible.Port,
                ["user"] = ansible.User,
                ["hostKey"] = hostKey,
                // A container's pin is the fingerprints Proxmox logged when it made the host keys: see ContainerHostKeys.
                ["hostKeyFingerprints"] = ownPin,
            },
            ["vars"] = rotation is null ? ansible.Vars.DeepClone() : await RotationVarsAsync(run, rotation, step.Name, cancellationToken),
            ["context"] = new JsonObject
            {
                ["address"] = context.Address,
                ["commit"] = context.Commit,
                ["action"] = ansible.Action,
                ["run"] = run.Id.ToString(),
            },
            ["guestDirectory"] = ansible.GuestDirectory,
            ["outcomes"] = new JsonArray(ansible.Outcomes.Keys.Select(k => (JsonNode?)k).ToArray()),
            ["timeoutMinutes"] = ansible.TimeoutMinutes,
            ["manifest"] = rotation is null ? Manifest(run, context) : await RotationManifestAsync(run, context, rotation, cancellationToken),
            // A name, for the check to resolve; the values never reach a preview.
            ["secretVars"] = new JsonObject(ansible.SecretVars.Select(n => KeyValuePair.Create(n, (JsonNode?)SecretNames.AnsibleVar(n)))),
            ["secretFiles"] = new JsonObject(ansible.SecretFiles.Select(n => KeyValuePair.Create(n, (JsonNode?)SecretNames.AnsibleFile(n)))),
        };
        if (snapshot is not null)
            input["prepared"] = JsonNode.Parse(snapshot);
        if (step.Name != StepNames.Preview)
            input["sshKey"] = SecretNames.AnsibleSshKey;
        if (step.Name == StepNames.ConfirmRefused)
            input["expectRefused"] = SecretNames.AnsibleRefusedKeys;
        if (outcome?.ApproveVars is { } vars)
            input["approvedVars"] = vars.DeepClone();

        var assignment = new Assignment
        {
            AttemptId = attempt.Id.ToString(),
            Executor = "ansible",
            Action = step.Name == StepNames.Preview ? "preview" : "run",
            LeaseSeconds = (uint)options.Value.AgentChannel.LeaseSeconds,
            InputJson = input.ToJsonString(),
            FilesTarGz = ByteString.CopyFrom(rotation is null ? await bundles.ForAsync(run, context, cancellationToken) : GuestAccessPlaybooks.Bundle()),
        };
        if (preview is not null)
            assignment.Artifacts.Add(preview);
        foreach (var (name, value) in values)
        {
            assignment.Secrets[name] = value;
        }

        return assignment;
    }

    /// <summary>What a run is built from, in the document an approval names. It holds store keys and versions, never values.</summary>
    private static JsonObject Manifest(Run run, RunContext context)
    {
        var ansible = context.Ansible!;
        return new JsonObject
        {
            ["commit"] = context.Commit,
            ["inputsHash"] = run.InputsHash,
            ["workflow"] = ansible.Workflow,
            // Which files the label stands for: a release's commit in its repository, or the lab repository's own.
            ["workflowCommit"] = context.Workflow?.Commit,
            ["workflowsRepository"] = context.Workflow?.Repository,
            ["root"] = ansible.Root,
            ["image"] = ansible.Image,
            ["host"] = $"{ansible.User}@{ansible.Address}:{ansible.Port}",
            ["destructive"] = ansible.Destructive,
            ["vars"] = ansible.Vars.DeepClone(),
            ["secrets"] = new JsonObject(context.Secrets.Keys.Where(k => k != SecretNames.AnsibleHostKey).Order(StringComparer.Ordinal)
                .Select(name => KeyValuePair.Create(name, (JsonNode?)$"{context.Secrets[name]} v{context.SecretVersions[name]}"))),
            ["changedFiles"] = ansible.ChangedFiles is { } files ? new JsonArray(files.Select(f => (JsonNode?)f).ToArray()) : null,
        };
    }

    /// <summary>The keys a step of a rotation works with. Removal names each by fingerprint, for the guest's OpenSSH to compare.</summary>
    private async Task<JsonObject> RotationVarsAsync(Run run, AccessRotationContext rotation, string stepName, CancellationToken cancellationToken)
    {
        var keys = await db.GuestAccessKeys.AsNoTracking().Where(k => k.ResourceId == run.ResourceId).ToDictionaryAsync(k => k.Version, cancellationToken);
        var vars = new JsonObject
        {
            ["access_candidate_key"] = keys[rotation.Candidate].PublicKey,
            ["access_kept_key"] = keys[rotation.Candidate].Fingerprint,
        };
        if (stepName is StepNames.RemoveKey or StepNames.VerifyRemoval)
            vars["access_removed_keys"] = new JsonArray(rotation.Remove.Select(v => (JsonNode?)keys[v].Fingerprint).ToArray());
        return vars;
    }

    /// <summary>What a person approves: which key goes on, and which come off. An approval names this document.</summary>
    private async Task<JsonObject> RotationManifestAsync(Run run, RunContext context, AccessRotationContext rotation, CancellationToken cancellationToken)
    {
        var keys = await db.GuestAccessKeys.AsNoTracking().Where(k => k.ResourceId == run.ResourceId).ToDictionaryAsync(k => k.Version, cancellationToken);
        var manifest = Manifest(run, context);
        manifest["accessRotation"] = new JsonObject
        {
            ["current"] = keys[rotation.Current].Fingerprint,
            ["candidate"] = keys[rotation.Candidate].Fingerprint,
            ["remove"] = new JsonArray(rotation.Remove.Select(v => (JsonNode?)keys[v].Fingerprint).ToArray()),
        };
        return manifest;
    }

    /// <summary>The preview the run's approval names; any other is refused, as an apply refuses another plan.</summary>
    private async Task<Artifact> ApprovedPreviewAsync(Run run, Step step, CancellationToken cancellationToken)
    {
        var steps = await db.Steps.AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync(cancellationToken);
        var preview = steps.SingleOrDefault(s => s.Name == StepNames.Preview);
        var approval = steps.SingleOrDefault(s => s.Name == StepNames.Approval);
        if (preview?.Status != StepStatuses.Succeeded || approval?.Status != StepStatuses.Succeeded || approval.Decision != "approved")
            throw new AssignmentException("A playbook requires a successful preview and approval for this run. Run it again.");
        // A lost result means a task may have run: only a known end lets another attempt start.
        if (run.Status == RunStatuses.Uncertain || step.Status == StepStatuses.Uncertain)
            throw new AssignmentException("An uncertain playbook cannot be replayed; inspect the guest first.");
        var attempt = await db.Attempts.AsNoTracking()
            .Where(a => a.StepId == preview.Id && a.Status == AttemptStatuses.Succeeded)
            .OrderByDescending(a => a.Number)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new AssignmentException("The preview has no successful attempt.");
        var record = await db.Artifacts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.AttemptId == attempt.Id && a.Name == PreviewArtifact, cancellationToken)
            ?? throw new AssignmentException("The preview has no saved document.");
        if (string.IsNullOrWhiteSpace(record.Sha256) || approval.PlanSha256 != record.Sha256)
            throw new AssignmentException("The saved preview is not the one that was approved.");
        var content = await artifacts.LoadAsync(attempt.Id, PreviewArtifact, cancellationToken);
        if (ArtifactStore.Sha256(content) != record.Sha256)
            throw new AssignmentException("The saved preview does not match its recorded checksum.");
        return new Artifact { Name = PreviewArtifact, Content = ByteString.CopyFrom(content), Sha256 = record.Sha256 };
    }

    /// <summary>The outcome a person approved, whose vars the further run gets.</summary>
    private async Task<AnsibleOutcome> ApprovedOutcomeAsync(Run run, CancellationToken cancellationToken)
    {
        var steps = await db.Steps.AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync(cancellationToken);
        var approval = steps.SingleOrDefault(s => s.Name == StepNames.OutcomeApproval);
        if (approval?.Status != StepStatuses.Succeeded || approval.Decision != "approved" || approval.DecidedBy is null or "policy")
            throw new AssignmentException("A further run needs a person's approval of the reported outcome.");
        var withSteps = new Run { Operation = run.Operation, Reason = run.Reason, Context = run.Context, Steps = steps };
        return RunEngine.OutcomeOf(withSteps)?.Declared
            ?? throw new AssignmentException("The playbook reported no outcome to approve.");
    }
}
