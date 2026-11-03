using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Store;
using Octopushy.Server.Options;

namespace Octopushy.Server.Orchestration;

/// <summary>Builds runs and their steps, and observations, from a revision's definitions.</summary>
public sealed partial class RunFactory(IOptions<OctopushyOptions> options, TimeProvider clock)
{
    /// <summary>A provision run (plan, approval, unprotect when the plan removes the VM, apply, verify) or, for a retired guest, a retire run.</summary>
    /// <remarks><paramref name="keyIssued"/> is whether Octopushy already holds a key for the guest, as <see cref="GuestAccessCredentials.IssuedAsync"/> says.</remarks>
    public Run Guest(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision, string reason, bool keyIssued) =>
        CreateGuest(repository, guest, resource, revision, reason, null, keyIssued);

    /// <summary>
    /// Removes a guest whose folder was deleted: a retire run built from <paramref name="lastDefined"/>,
    /// the last revision that defined it, so it destroys exactly what that definition created.
    /// </summary>
    public Run RemoveDeleted(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision lastDefined,
        string removedIn, string reason, bool keyIssued) =>
        CreateGuest(repository, guest, resource, lastDefined, reason, removedIn, keyIssued);

    private Run CreateGuest(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision, string reason,
        string? removedIn, bool keyIssued)
    {
        var removal = removedIn is not null;
        if (removal && guest.Retired)
            throw new RunException("Only a guest whose definition was deleted can be removed this way.");
        var inputs = ProvisionInputs.Build(repository, guest, revision.Commit, keyIssued);
        var policy = PolicyResolver.For(repository, guest);
        var labels = PolicyResolver.AgentLabels(repository, guest);
        var connection = inputs.Installation.Connection;
        var endpoint = ProvisionInputs.EndpointFor(inputs.Installation, inputs.Host);

        var readRole = connection.SharedToken ? SecretNames.ProxmoxSharedToken : SecretNames.ProxmoxReadOnlyToken;
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [readRole] = connection.ReadOnlyToken.Key,
        };
        if (!connection.SharedToken)
            secrets[SecretNames.ProxmoxPrivilegedToken] = connection.PrivilegedToken.Key;
        var requestedVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        if (connection.ReadOnlyToken.Version is { } tokenVersion)
        {
            requestedVersions[readRole] = tokenVersion;
        }
        if (!connection.SharedToken && connection.PrivilegedToken.Version is { } privilegedVersion)
        {
            requestedVersions[SecretNames.ProxmoxPrivilegedToken] = privilegedVersion;
        }
        if (connection.CaCertificate is { } ca)
        {
            secrets[SecretNames.ProxmoxCa] = ca.Key;
            if (ca.Version is { } caVersion)
            {
                requestedVersions[SecretNames.ProxmoxCa] = caVersion;
            }
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PROXMOX_VE_ENDPOINT"] = endpoint,
            ["PROXMOX_VE_INSECURE"] = connection.VerifyTls ? "false" : "true",
        };
        var secretEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PROXMOX_VE_API_TOKEN"] = SecretNames.ProxmoxToken,
        };
        var retire = guest.Retired || removal;
        var context = new RunContext
        {
            Address = guest.Address,
            Commit = revision.Commit,
            AgentLabels = labels.Value,
            AgentLabelsSetBy = labels.SetBy,
            HostKey = $"{inputs.Installation.Address}/{inputs.Host.Id}",
            HostLimit = inputs.Host.ConcurrentOperations,
            Policy = new PolicyContext(policy.Approval.Value, policy.Approval.SetBy, policy.Destructive.Value, policy.Destructive.SetBy, policy.DestructiveExplicit),
            DefinitionRemovedIn = removedIn,
            ManagedAccess = ManagedAccess.Wanted(repository, guest, keyIssued),
            OpenTofu = new OpenTofuContext(
                guest.Stack,
                inputs.Recipe.Root,
                inputs.Recipe.Image ?? options.Value.Tools.OpenTofuImage,
                inputs.Variables,
                env,
                secretEnv),
            Proxmox = new ProxmoxContext(endpoint, connection.VerifyTls, connection.CaCertificate?.Key, guest.Host, guest.Vmid, guest.Id, ProvisionInputs.ExpectedIpv4(guest)),
            Secrets = secrets,
            RequestedSecretVersions = requestedVersions,
            Verify = inputs.Recipe.Verify,
        };

        var run = NewRun(resource, retire ? Operations.Retire : Operations.Provision, revision, reason, context);
        run.InputsHash = retire ? RetireHash(inputs.Hash) : inputs.Hash;
        var steps = retire
            ? new[] { StepNames.Plan, StepNames.Approval, StepNames.Unprotect, StepNames.Apply, StepNames.VerifyAbsent }
            : [StepNames.Plan, StepNames.Approval, StepNames.Unprotect, StepNames.Apply, StepNames.Verify];
        AddSteps(run, steps);
        if (context.Verify == "none")
        {
            foreach (var step in run.Steps.Where(s => s.Name is StepNames.Verify or StepNames.VerifyAbsent))
            {
                step.Status = StepStatuses.Skipped;
                step.Detail = "The recipe does not verify through Proxmox.";
            }
        }

        return run;
    }

    /// <summary>
    /// A configure run: one action of the guest's setup recipe. It reaches the guest over SSH, so
    /// it holds no Proxmox credentials. The installation's endpoint stays in its context because
    /// trust in that installation still decides whether its guests are worked on.
    /// </summary>
    public Run Configure(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision,
        string actionId, string reason, GuestConfiguration? configuration, IReadOnlyList<string>? changedFiles, DateTimeOffset? scheduledFor = null)
    {
        if (guest.Retired)
            throw new RunException("A retired guest is not configured.");
        var inputs = ConfigureInputs.Build(repository, guest);
        if (!inputs.Configure.Actions.TryGetValue(actionId, out var action))
            throw new RunException($"The recipe {inputs.Recipe.Id} has no action {actionId}.");
        if (action.UsesAppliedSnapshot && configuration?.Snapshot is null)
            throw new RunException($"{actionId} reuses what the last successful run prepared, and this guest has none yet. Run an action that prepares one first.");

        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        var requestedVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        void Bind(string role, StoreReference reference)
        {
            secrets[role] = reference.Key;
            if (reference.Version is { } version)
            {
                requestedVersions[role] = version;
            }
        }

        if (inputs.SshKey is { } declared)
            Bind(SecretNames.AnsibleSshKey, declared);
        else
            secrets[SecretNames.AnsibleSshKey] = GuestAccessCredentials.Key(resource.Id);
        Bind(SecretNames.AnsibleHostKey, inputs.HostKey);
        foreach (var (name, reference) in inputs.SecretVars)
        {
            Bind(SecretNames.AnsibleVar(name), reference);
        }

        foreach (var (name, reference) in inputs.SecretFiles)
        {
            Bind(SecretNames.AnsibleFile(name), reference);
        }

        var installation = repository.Installation(guest.InstallationAddress)!;
        var policy = PolicyResolver.For(repository, guest);
        var labels = PolicyResolver.AgentLabels(repository, guest);
        var context = new RunContext
        {
            Address = guest.Address,
            Commit = revision.Commit,
            AgentLabels = labels.Value,
            AgentLabelsSetBy = labels.SetBy,
            Policy = new PolicyContext(policy.Approval.Value, policy.Approval.SetBy, policy.Destructive.Value, policy.Destructive.SetBy),
            Proxmox = new ProxmoxContext(ProvisionInputs.EndpointFor(installation, installation.Hosts[guest.Host]), installation.Connection.VerifyTls, null,
                guest.Host, guest.Vmid, guest.Id, null),
            Secrets = secrets,
            RequestedSecretVersions = requestedVersions,
            ScheduledFor = scheduledFor,
            Ansible = new AnsibleContext(
                inputs.Recipe.Id,
                action.Id,
                inputs.Recipe.Root,
                action.Playbook,
                action.Prepare,
                action.UsesAppliedSnapshot,
                action.Destructive,
                action.TimeoutMinutes,
                inputs.Recipe.Image ?? options.Value.Tools.AnsibleImage,
                guest.Id,
                guest.Directory,
                inputs.Address,
                inputs.Port,
                inputs.User,
                inputs.Vars,
                inputs.SecretVars.Keys.ToList(),
                inputs.SecretFiles.Keys.ToList(),
                inputs.Configure.Outcomes.ToDictionary(
                    o => o.Key, o => new AnsibleOutcome(o.Value.Description, o.Value.ApproveVars, o.Value.Result), StringComparer.Ordinal),
                changedFiles),
        };

        var run = NewRun(resource, Operations.Configure, revision, reason, context);
        run.InputsHash = inputs.Hash;
        AddSteps(run, inputs.Configure.Outcomes.Values.Any(o => o.ApproveVars is not null)
            ? [StepNames.Preview, StepNames.Approval, StepNames.Playbook, StepNames.OutcomeApproval, StepNames.PlaybookApproved]
            : [StepNames.Preview, StepNames.Approval, StepNames.Playbook]);
        return run;
    }

    /// <summary>
    /// A rotation of a guest's own key. Its login is its own binding: each step takes the version
    /// it needs, so no Proxmox credential and no setup secret is in it, only the pin.
    /// </summary>
    public Run RotateAccess(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision, AccessRotationContext rotation)
    {
        var inputs = ConfigureInputs.Build(repository, guest);
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal) { [SecretNames.AnsibleHostKey] = inputs.HostKey.Key };
        var requestedVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        if (inputs.HostKey.Version is { } hostVersion)
            requestedVersions[SecretNames.AnsibleHostKey] = hostVersion;
        var installation = repository.Installation(guest.InstallationAddress)!;
        var policy = PolicyResolver.For(repository, guest);
        var labels = PolicyResolver.AgentLabels(repository, guest);
        var context = new RunContext
        {
            Address = guest.Address,
            Commit = revision.Commit,
            AgentLabels = labels.Value,
            AgentLabelsSetBy = labels.SetBy,
            Policy = new PolicyContext(policy.Approval.Value, policy.Approval.SetBy, policy.Destructive.Value, policy.Destructive.SetBy),
            Proxmox = new ProxmoxContext(ProvisionInputs.EndpointFor(installation, installation.Hosts[guest.Host]), installation.Connection.VerifyTls, null,
                guest.Host, guest.Vmid, guest.Id, null),
            Secrets = secrets,
            RequestedSecretVersions = requestedVersions,
            AccessRotation = rotation,
            Ansible = new AnsibleContext(
                GuestAccessPlaybooks.Root,
                Operations.RotateAccess,
                GuestAccessPlaybooks.Root,
                GuestAccessPlaybooks.Authorize,
                null,
                false,
                false,
                10,
                options.Value.Tools.AnsibleImage,
                guest.Id,
                guest.Directory,
                inputs.Address,
                inputs.Port,
                inputs.User,
                [],
                [],
                [],
                [],
                null),
        };

        var run = NewRun(resource, Operations.RotateAccess, revision, "manual", context);
        run.InputsHash = inputs.Hash;
        AddSteps(run, [
            StepNames.Preview, StepNames.Approval, StepNames.VerifyCurrent, StepNames.AuthorizeKey, StepNames.VerifyKey,
            StepNames.PromoteKey, StepNames.ApproveRemoval, StepNames.RemoveKey, StepNames.VerifyRemoval, StepNames.ConfirmRefused]);
        return run;
    }

    /// <summary>What an observation of the installation's hosts and VMs through its Proxmox API is built from.</summary>
    public static RunContext Observation(LabRepository repository, InstallationDefinition installation, DefinitionRevision revision)
    {
        var labels = PolicyResolver.AgentLabels(repository, installation);
        var connection = installation.Connection;
        var readRole = connection.SharedToken ? SecretNames.ProxmoxSharedToken : SecretNames.ProxmoxReadOnlyToken;
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [readRole] = connection.ReadOnlyToken.Key,
        };
        var requestedVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        if (connection.ReadOnlyToken.Version is { } tokenVersion)
        {
            requestedVersions[readRole] = tokenVersion;
        }
        if (connection.CaCertificate is { } ca)
        {
            secrets[SecretNames.ProxmoxCa] = ca.Key;
            if (ca.Version is { } caVersion)
            {
                requestedVersions[SecretNames.ProxmoxCa] = caVersion;
            }
        }

        return new RunContext
        {
            Address = installation.Address,
            Commit = revision.Commit,
            AgentLabels = labels.Value,
            AgentLabelsSetBy = labels.SetBy,
            Proxmox = new ProxmoxContext(connection.Endpoint, connection.VerifyTls, connection.CaCertificate?.Key, null, null, null, null),
            Secrets = secrets,
            RequestedSecretVersions = requestedVersions,
        };
    }

    public static string RetireHash(string inputsHash) => "retire:" + inputsHash;

    public static string InstallationHash(InstallationDefinition installation) => Canonical.Hash(installation.Json);

    private Run NewRun(Resource resource, string operation, DefinitionRevision revision, string reason, RunContext context) => new()
    {
        Id = Guid.CreateVersion7(),
        ResourceId = resource.Id,
        Operation = operation,
        RevisionId = revision.Id,
        Commit = revision.Commit,
        Reason = reason,
        Context = context.Serialize(),
        CreatedAt = clock.GetUtcNow(),
    };

    private static void AddSteps(Run run, IReadOnlyList<string> names)
    {
        for (var i = 0; i < names.Count; i++)
        {
            run.Steps.Add(new Step
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                Ordinal = i,
                Name = names[i],
                Kind = names[i] is StepNames.Approval or StepNames.OutcomeApproval or StepNames.PromoteKey or StepNames.ApproveRemoval
                    ? StepKinds.Approval : StepKinds.Agent,
            });
        }
    }
}
