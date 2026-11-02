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

    /// <summary>
    /// A preview gets only the pin, which is public: it runs repository code that nobody has
    /// approved. The rest goes to the steps after approval.
    /// </summary>
    private static List<string> AnsibleSecretsFor(string stepName, RunContext context) =>
        stepName == StepNames.Preview ? [SecretNames.AnsibleHostKey] : context.Secrets.Keys.ToList();

    /// <summary>Builds a configure run's attempt, after the checks every assignment goes through.</summary>
    private async Task<Assignment> BuildAnsibleAsync(Attempt attempt, Step step, Run run, RunContext context, Guid agentId, CancellationToken cancellationToken)
    {
        var ansible = context.Ansible ?? throw new AssignmentException("Missing configure context.");
        // Check authority and artifact integrity BEFORE resolving any plaintext.
        var outcome = step.Name == StepNames.PlaybookApproved ? await ApprovedOutcomeAsync(run, cancellationToken) : null;
        var preview = step.Name == StepNames.Preview ? null : await ApprovedPreviewAsync(run, step, cancellationToken);
        // A removal is what a deleted folder asks for, so it is the one run a missing resource gets.
        if (!await db.Resources.AnyAsync(r => r.Id == run.ResourceId && r.RetiredAt == null && (r.MissingSince == null || ansible.Removal), cancellationToken))
            throw new AssignmentException("The guest or service is missing or retired.");
        var snapshot = ansible.UsesAppliedSnapshot && step.Name == StepNames.Preview
            ? await db.GuestConfigurations.AsNoTracking().Where(c => c.ResourceId == run.ResourceId).Select(c => c.Snapshot).FirstOrDefaultAsync(cancellationToken)
                ?? throw new AssignmentException("This action reuses what the last successful run prepared, and the guest has none yet.")
            : null;

        var names = SecretsFor(step.Name, context);
        var problems = await CredentialBindings.CheckAsync(db, run, context, cancellationToken, names);
        if (problems.Count > 0)
            throw new CredentialUnavailableException($"Blocked credentials: {string.Join(", ", problems)}.");
        var (values, missing) = await store.ResolveForAttemptAsync(
            names.ToDictionary(n => n, n => new Definitions.StoreReference(context.Secrets[n], context.SecretVersions[n]), StringComparer.Ordinal),
            names.Where(CredentialBindings.RequiresSecret).ToHashSet(StringComparer.Ordinal),
            attempt.Id,
            agentId,
            new SecretRelease(run.ResourceId, context.CodeDigest, context.Address),
            cancellationToken);
        if (missing.Count > 0)
            throw new CredentialUnavailableException($"Blocked credentials: {string.Join(", ", missing)}");
        values.Remove(SecretNames.AnsibleHostKey, out var hostKey);
        if (string.IsNullOrWhiteSpace(hostKey))
            throw new CredentialUnavailableException($"Blocked credentials: {context.Secrets[SecretNames.AnsibleHostKey]}: empty");
        // Code outside what was reviewed cannot run if it never arrives.
        var reviewed = await ProtectedSecrets.AnyAsync(db, context.Secrets.Values, cancellationToken);
        if (reviewed && context.ReviewedPaths is null)
            throw new CredentialUnavailableException("This run holds a protected secret and has no reviewed code; run it again.");

        var input = new JsonObject
        {
            ["image"] = ansible.Image,
            ["root"] = ansible.Root,
            ["action"] = ansible.Action,
            ["playbook"] = ansible.Playbook,
            ["prepare"] = ansible.Prepare,
            ["host"] = new JsonObject
            {
                ["name"] = ansible.GuestName,
                ["address"] = ansible.Address,
                ["port"] = ansible.Port,
                ["user"] = ansible.User,
                ["hostKey"] = hostKey,
            },
            ["vars"] = ansible.Vars.DeepClone(),
            ["context"] = new JsonObject
            {
                ["address"] = context.Address,
                ["commit"] = context.Commit,
                ["action"] = ansible.Action,
                ["run"] = run.Id.ToString(),
                ["service"] = ansible.Service,
                ["ports"] = ansible.Ports?.DeepClone() ?? new JsonArray(),
            },
            ["guestDirectory"] = ansible.GuestDirectory,
            ["outcomes"] = new JsonArray(ansible.Outcomes.Keys.Select(k => (JsonNode?)k).ToArray()),
            ["timeoutMinutes"] = ansible.TimeoutMinutes,
            ["manifest"] = Manifest(run, context),
            // A name, for the check to resolve; the values never reach a preview.
            ["secretVars"] = new JsonObject(ansible.SecretVars.Select(n => KeyValuePair.Create(n, (JsonNode?)SecretNames.AnsibleVar(n)))),
            ["secretFiles"] = new JsonObject(ansible.SecretFiles.Select(n => KeyValuePair.Create(n, (JsonNode?)SecretNames.AnsibleFile(n)))),
        };
        if (snapshot is not null)
            input["prepared"] = JsonNode.Parse(snapshot);
        if (step.Name != StepNames.Preview)
            input["sshKey"] = SecretNames.AnsibleSshKey;
        if (outcome?.ApproveVars is { } vars)
            input["approvedVars"] = vars.DeepClone();

        var assignment = new Assignment
        {
            AttemptId = attempt.Id.ToString(),
            Executor = "ansible",
            Action = step.Name == StepNames.Preview ? "preview" : "run",
            LeaseSeconds = (uint)options.Value.AgentChannel.LeaseSeconds,
            InputJson = input.ToJsonString(),
            FilesTarGz = ByteString.CopyFrom(reviewed
                ? await mirror.BundleAsync(run.Commit!, context.ReviewedPaths!, cancellationToken)
                : await mirror.BundleAsync(run.Commit!, cancellationToken)),
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
            ["recipe"] = ansible.Recipe,
            ["root"] = ansible.Root,
            ["image"] = ansible.Image,
            ["host"] = $"{ansible.User}@{ansible.Address}:{ansible.Port}",
            ["destructive"] = ansible.Destructive,
            ["service"] = ansible.Service,
            ["ports"] = ansible.Ports?.DeepClone(),
            ["vars"] = ansible.Vars.DeepClone(),
            ["secrets"] = new JsonObject(context.Secrets.Keys.Where(k => k != SecretNames.AnsibleHostKey).Order(StringComparer.Ordinal)
                .Select(name => KeyValuePair.Create(name, (JsonNode?)$"{context.Secrets[name]} v{context.SecretVersions[name]}"))),
            ["changedFiles"] = ansible.ChangedFiles is { } files ? new JsonArray(files.Select(f => (JsonNode?)f).ToArray()) : null,
        };
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
