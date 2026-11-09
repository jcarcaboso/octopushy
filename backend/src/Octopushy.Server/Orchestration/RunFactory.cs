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
    /// <remarks>
    /// <paramref name="keyIssued"/> is whether Octopushy already holds a key for the guest, as <see cref="GuestAccessCredentials.IssuedAsync"/> says,
    /// and <paramref name="hostKeyIssued"/> the same for its host key, as <see cref="GuestHostCredentials.IssuedAsync"/> says.
    /// </remarks>
    public Run Guest(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision, string reason, bool keyIssued,
        bool hostKeyIssued = false) =>
        CreateGuest(repository, guest, resource, revision, reason, null, keyIssued, hostKeyIssued);

    /// <summary>
    /// Removes a guest whose folder was deleted: a retire run built from <paramref name="lastDefined"/>,
    /// the last revision that defined it, so it destroys exactly what that definition created.
    /// </summary>
    public Run RemoveDeleted(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision lastDefined,
        string removedIn, string reason, bool keyIssued) =>
        CreateGuest(repository, guest, resource, lastDefined, reason, removedIn, keyIssued, false);

    private Run CreateGuest(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision, string reason,
        string? removedIn, bool keyIssued, bool hostKeyIssued)
    {
        var removal = removedIn is not null;
        if (removal && guest.Retired)
            throw new RunException("Only a guest whose definition was deleted can be removed this way.");
        Managed(repository, guest.InstallationAddress);
        var inputs = ProvisionInputs.Build(repository, guest, revision.Commit, keyIssued, hostKeyIssued);
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
        // A guest whose host key is Octopushy's: the plan gets the private half to write into the snippet, and the apply
        // gets the upload key and the host's own key to pin. A retire deletes the snippet through the API and needs none.
        var snippets = !retire && ManagedHostKey.Wanted(repository, guest, hostKeyIssued) ? ManagedHostKey.Settings(inputs.Installation) : null;
        SnippetsContext? uploads = null;
        if (snippets is not null)
        {
            var node = snippets.Hosts[guest.Host];
            secrets[SecretNames.GuestHostKey] = GuestHostCredentials.Key(resource.Id);
            secrets[SecretNames.SnippetsKey] = InstallationSnippetCredentials.Key(inputs.Installation.Address);
            secrets[SecretNames.SnippetsHostKey] = node.HostKey.Key;
            if (node.HostKey.Version is { } pinVersion)
                requestedVersions[SecretNames.SnippetsHostKey] = pinVersion;
            env[SecretNames.SnippetsUserEnv] = snippets.User;
            secretEnv[SecretNames.GuestHostKeyEnv] = SecretNames.GuestHostKey;
            secretEnv[SecretNames.SnippetsKeyEnv] = SecretNames.SnippetsKey;
            uploads = new SnippetsContext(inputs.Installation.Address, snippets.User, node.Address, node.Port);
        }
        else
            inputs.Variables.Remove("octopushy_snippets");
        var context = new RunContext
        {
            Address = guest.Address,
            Commit = revision.Commit,
            Workflow = WorkflowContext.Of(inputs.Workflow, guest.WorkflowReference, revision.Commit),
            AgentLabels = labels.Value,
            AgentLabelsSetBy = labels.SetBy,
            HostKey = $"{inputs.Installation.Address}/{inputs.Host.Id}",
            HostLimit = inputs.Host.ConcurrentOperations,
            Policy = new PolicyContext(policy.Approval.Value, policy.Approval.SetBy, policy.Destructive.Value, policy.Destructive.SetBy, policy.DestructiveExplicit),
            DefinitionRemovedIn = removedIn,
            ManagedAccess = ManagedAccess.Wanted(repository, guest, keyIssued),
            ContainerHostKey = !retire && ContainerHostKey.Wanted(repository, guest),
            Snippets = uploads,
            OpenTofu = new OpenTofuContext(
                guest.Stack,
                inputs.Stage.Root,
                inputs.Stage.Image ?? options.Value.Tools.OpenTofuImage,
                inputs.Variables,
                env,
                secretEnv),
            Proxmox = new ProxmoxContext(endpoint, connection.VerifyTls, connection.CaCertificate?.Key, guest.Host, guest.Vmid, guest.Id, ProvisionInputs.ExpectedIpv4(guest),
                guest.ProxmoxKind),
            Secrets = secrets,
            RequestedSecretVersions = requestedVersions,
            Verify = inputs.Stage.Verify,
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
                step.Detail = $"The workflow does not verify this {(guest.Type == GuestTypes.Container ? "container" : "VM")} through Proxmox.";
            }
        }

        return run;
    }

    /// <summary>
    /// A configure run: one action of the setup stage of the guest's workflow. It reaches the guest over SSH, so
    /// it holds no Proxmox credentials. The installation's endpoint stays in its context because
    /// trust in that installation still decides whether its guests are worked on.
    /// </summary>
    public Run Configure(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision,
        string actionId, string reason, GuestConfiguration? configuration, IReadOnlyList<string>? changedFiles, DateTimeOffset? scheduledFor = null)
    {
        if (guest.Retired)
            throw new RunException("A retired guest is not configured.");
        Managed(repository, guest.InstallationAddress);
        var inputs = ConfigureInputs.Build(repository, guest);
        if (!inputs.Configure.Actions.TryGetValue(actionId, out var action))
            throw new RunException($"The workflow {inputs.Workflow.Label} has no action {actionId}.");
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
        // One trust source, chosen where every run that logs in chooses it: see BindHostKey.
        var managedHostKey = BindHostKey(repository, guest, resource.Id, pin => Bind(SecretNames.AnsibleHostKey, pin)) == HostKeyTrust.Managed;
        foreach (var (name, reference) in inputs.SecretVars)
        {
            Bind(SecretNames.AnsibleVar(name), reference);
        }

        foreach (var (name, reference) in inputs.SecretFiles)
        {
            Bind(SecretNames.AnsibleFile(name), reference);
        }

        var installation = repository.Installation(guest.InstallationAddress)!;
        var policy = PolicyResolver.ForAction(repository, guest, action.Id);
        var labels = PolicyResolver.AgentLabels(repository, guest);
        var context = new RunContext
        {
            Address = guest.Address,
            Commit = revision.Commit,
            Workflow = WorkflowContext.Of(inputs.Workflow, guest.WorkflowReference, revision.Commit),
            AgentLabels = labels.Value,
            AgentLabelsSetBy = labels.SetBy,
            Policy = new PolicyContext(policy.Approval.Value, policy.Approval.SetBy, policy.Destructive.Value, policy.Destructive.SetBy),
            Proxmox = new ProxmoxContext(ProvisionInputs.EndpointFor(installation, installation.Hosts[guest.Host]), installation.Connection.VerifyTls, null,
                guest.Host, guest.Vmid, guest.Id, null),
            Secrets = secrets,
            RequestedSecretVersions = requestedVersions,
            ScheduledFor = scheduledFor,
            ManagedHostKey = managedHostKey,
            PreviousPrepared = configuration?.Snapshot is { } snapshot ? JsonNode.Parse(snapshot) : null,
            Ansible = new AnsibleContext(
                inputs.Workflow.Label,
                action.Id,
                inputs.Stage.Root,
                action.Playbook,
                action.Prepare,
                action.UsesAppliedSnapshot,
                action.Destructive,
                action.TimeoutMinutes,
                inputs.Stage.Image ?? options.Value.Tools.AnsibleImage,
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
    /// it needs, so no Proxmox credential and no setup secret is in it, only the pin. It needs only how the guest is
    /// reached, so it works for any guest Octopushy holds a key for, whatever Ansible work gave it one: its setup stage
    /// or a service's stage alike.
    /// </summary>
    public Run RotateAccess(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision, AccessRotationContext rotation)
    {
        Managed(repository, guest.InstallationAddress);
        var address = ConfigureInputs.AddressOf(guest)
            ?? throw new RunException("This guest has no address to reach it at: set access.address, or a static or reserved address.");
        var user = guest.Json["access"]?["user"]?.GetValue<string>()
            ?? throw new RunException("This guest names no access.user to log in as.");
        // One trust source, chosen where every run that logs in chooses it: see BindHostKey.
        StoreReference? hostKey = null;
        var managedHostKey = BindHostKey(repository, guest, resource.Id, pin => hostKey = pin) == HostKeyTrust.Managed;
        var port = guest.Json["access"]?["port"]?.GetValue<int>() ?? 22;
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        var requestedVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        if (hostKey is not null)
        {
            secrets[SecretNames.AnsibleHostKey] = hostKey.Key;
            if (hostKey.Version is { } hostVersion)
                requestedVersions[SecretNames.AnsibleHostKey] = hostVersion;
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
            AccessRotation = rotation,
            ManagedHostKey = managedHostKey,
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
                address,
                port,
                user,
                [],
                [],
                [],
                [],
                null),
        };

        var run = NewRun(resource, Operations.RotateAccess, revision, "manual", context);
        // What the rotation reaches the guest by; its playbooks are the server's own.
        run.InputsHash = Canonical.Hash(new JsonObject
        {
            ["access"] = guest.Json["access"]?.DeepClone(),
            ["address"] = address,
            ["rotation"] = new JsonObject { ["current"] = rotation.Current, ["candidate"] = rotation.Candidate },
        });
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

    /// <summary>
    /// Whether a run that logs in to the guest pins Octopushy's key rather than the guest's <c>access.hostKey</c>. The
    /// choice is explicit: a missing <c>access.hostKey</c> alone never means Octopushy's key, only a VM whose workflow
    /// delivers one to a host set up for it does, and anything else is refused.
    /// </summary>
    internal static bool PinsManagedHostKey(LabRepository repository, GuestDefinition guest) =>
        BindHostKey(repository, guest, Guid.Empty, _ => { }) == HostKeyTrust.Managed;

    /// <summary>
    /// The one place a run that logs in to a guest gets its host key from: setup, a service's Ansible stage, a pipeline and a
    /// rotation alike, their previews included. <see cref="ManagedHostKey.TrustOf(LabRepository, GuestDefinition)"/> decides,
    /// in its order: the guest's <c>access.hostKey</c>, bound as declared; a container's create-task pin, bound as its own
    /// <c>system/container-host-key/&lt;id&gt;</c>; or a VM's managed key, which binds no store entry, the run pinning the
    /// version an apply delivered instead. Anything else is refused. A run takes exactly one of them.
    /// </summary>
    internal static HostKeyTrust BindHostKey(LabRepository repository, GuestDefinition guest, Guid guestResourceId, Action<StoreReference> bind)
    {
        var trust = ManagedHostKey.TrustOf(repository, guest);
        switch (trust)
        {
            case HostKeyTrust.Declared:
                bind(ConfigureInputs.Reference(guest, "hostKey")!);
                break;
            case HostKeyTrust.Container:
                bind(new StoreReference(ContainerHostKeys.Key(guestResourceId), null));
                break;
            case HostKeyTrust.Managed:
                break;
            default:
                throw new RunException("This guest names no access.hostKey, and its host key is not Octopushy's: it is not a container, "
                    + "and its workflow does not deliver one to a VM on a host listed under its installation's snippets.hosts. Its host cannot be checked.");
        }

        return trust;
    }

    /// <summary>
    /// Refuses a run in an installation Octopushy only observes. Its guests are invalid already; this is
    /// asked again where every run is made, and the engine asks once more before anything is sent.
    /// </summary>
    private static void Managed(LabRepository repository, string installation)
    {
        if (repository.Installation(installation) is not { Managed: true })
            throw new RunException(repository.Installation(installation) is null
                ? $"The installation {installation} is not defined."
                : SafetyBoundary.ObservedOnlyReason(installation));
    }

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
