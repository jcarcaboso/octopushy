using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Options;

namespace Octopushy.Server.Orchestration;

/// <summary>Builds runs and their steps, and observations, from a revision's definitions.</summary>
public sealed class RunFactory(IOptions<OctopushyOptions> options, TimeProvider clock)
{
    /// <summary>A provision run (plan, approval, unprotect when the plan removes the VM or container, apply, verify) or, for a retired guest, a retire run.</summary>
    public Run Guest(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision, string reason) =>
        CreateGuest(repository, guest, resource, revision, reason, null, null);

    /// <summary>
    /// What a grant of a protected secret to this guest covers, as a run for it would carry it: the
    /// binding, which is the digest and what an operator is shown before trusting it, and the store keys
    /// the run binds, by the name it knows each by. It is built from the run itself, so it cannot drift
    /// from what the store checks when it releases a key.
    /// </summary>
    public (ReleaseBinding Binding, IReadOnlyDictionary<string, string> Secrets) Release(
        LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision)
    {
        var context = RunContext.Parse(Guest(repository, guest, resource, revision, "trust").Context);
        return (ReleaseBinding.Of(repository, guest), context.Secrets);
    }

    public Run UpdateWorker(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision, string reason, WorkerUpdateContext update) =>
        CreateGuest(repository, guest, resource, revision, reason, update, null);

    /// <summary>
    /// Removes a guest whose folder was deleted: a retire run built from <paramref name="lastDefined"/>,
    /// the last revision that defined it, so it destroys exactly what that definition created.
    /// A managed worker is drained first and its enrollment removed once its VM is gone.
    /// </summary>
    public Run RemoveDeleted(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision lastDefined,
        string removedIn, string reason) =>
        CreateGuest(repository, guest, resource, lastDefined, reason, null, removedIn);

    private Run CreateGuest(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision, string reason,
        WorkerUpdateContext? update, string? removedIn)
    {
        var removal = removedIn is not null;
        if (resource.ManagedWorker is { } bound && (guest.Retired || guest.ManagedWorker?.Generation != bound.Generation
            || guest.Host != bound.Host || guest.Vmid != bound.Vmid || guest.InstallationAddress != bound.Installation))
            throw new RunException("Managed workers cannot be retired, replaced, moved or have their worker role removed; retain the existing binding. To remove a worker, delete its folder.");
        if (guest.ManagedWorker is not null && guest.Retired)
            throw new RunException("Managed workers are not retired with lifecycle: retired; delete the worker's folder to remove it.");
        if (removal && (update is not null || guest.Retired))
            throw new RunException("Only a guest whose definition was deleted can be removed this way.");
        var inputs = ProvisionInputs.Build(repository, guest, revision.Commit);
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
        foreach (var (name, value) in inputs.Backend?.Env ?? new Dictionary<string, EnvValue>())
        {
            if (value.Store is { } store)
            {
                secrets[SecretNames.Backend(name)] = store.Key;
                if (store.Version is { } version)
                {
                    requestedVersions[SecretNames.Backend(name)] = version;
                }
                secretEnv[name] = SecretNames.Backend(name);
            }
            else
            {
                env[name] = value.Value!;
            }
        }

        // A retirement destroys what the guest has, which needs none of these. It must still
        // work once the key is gone, and the root's variable has to accept null for the plan.
        if (!guest.Retired && removedIn is null)
        {
            foreach (var (variable, store) in guest.ProvisionSecrets)
            {
                var name = $"TF_VAR_{variable}";
                secrets[SecretNames.Recipe(name)] = store.Key;
                if (store.Version is { } version)
                {
                    requestedVersions[SecretNames.Recipe(name)] = version;
                }
                secretEnv[name] = SecretNames.Recipe(name);
            }
        }

        var backend = inputs.Backend is { } external
            ? new BackendContext(
                external.Type,
                external.Config.ToDictionary(
                    p => p.Key,
                    p => p.Value is JsonValue v && v.TryGetValue<string>(out var text) ? JsonValue.Create(text.Replace("{stack}", guest.Stack)) : p.Value?.DeepClone(),
                    StringComparer.Ordinal),
                external.Workspace?.Replace("{stack}", guest.Stack))
            : BackendContext.BuiltIn;

        var retire = guest.Retired || removal;
        var reporter = options.Value.DiskReporter;
        reporter.Validate();
        var guestReporter = guest.Json["setup"]?["diskReporter"];
        // A container has no QEMU guest agent, and Proxmox has no exec for LXC, so nothing could
        // install the reporter. This is not an error: the guest simply reports no disk usage.
        var installReporter = !retire && !guest.IsContainer && reporter.Enabled
            && guestReporter?["enabled"]?.GetValue<bool>() != false;
        var nativeReporter = guest.ManagedWorker is not null;
        var image = inputs.Template?.Image;
        var verifiedImageHash = resource.ManagedWorker?.VerifiedOsUpdateRunId is not null
            ? resource.ManagedWorker.VerifiedOsImageSha256 : null;
        var nativeImageHash = update?.ImageSha256 ?? verifiedImageHash
            ?? (image?.ChecksumAlgorithm == "sha256" ? image.Checksum : null);
        var qualifiedNativeImage = reporter.SupportsManagedNixosImage(nativeImageHash);
        // An old immutable boot image cannot gain a helper by changing controller settings.
        // Never route a managed NixOS guest through the unrelated FHS downloader.
        if (installReporter && nativeReporter && !qualifiedNativeImage)
        {
            if (guestReporter?["enabled"]?.GetValue<bool>() == true)
                throw new RunException("Disk reporting requires a qualified native worker image pin; no FHS fallback is supported.");
            installReporter = false;
        }
        var reporterInterval = guestReporter?["intervalSeconds"]?.GetValue<int>() ?? reporter.IntervalSeconds;
        if (installReporter && nativeReporter && reporterInterval != 3600)
            throw new RunException("Native worker image v1 supports only a 3600-second disk reporting interval.");
        var context = new RunContext
        {
            Address = guest.Address,
            Commit = revision.Commit,
            AgentLabels = labels.Value,
            AgentLabelsSetBy = labels.SetBy,
            HostKey = $"{inputs.Installation.Address}/{inputs.Host.Id}",
            HostLimit = inputs.Host.ConcurrentOperations,
            Policy = new PolicyContext(guest.ManagedWorker is null ? policy.Approval.Value : "manual",
                guest.ManagedWorker is null ? policy.Approval.SetBy : "managed-worker safety", policy.Destructive.Value, policy.Destructive.SetBy, policy.DestructiveExplicit),
            ManagedWorker = guest.ManagedWorker is { } worker ? new ManagedWorkerContext(worker.Generation, worker.Labels,
                worker.MaxConcurrency, ManagedWorkerSafety.PublicUrl(options.Value.AgentChannel.PublicUrl)) : null,
            WorkerUpdate = update,
            DefinitionRemovedIn = removedIn,
            OpenTofu = new OpenTofuContext(
                guest.Stack,
                inputs.Recipe.Root,
                inputs.Recipe.Image ?? options.Value.Tools.OpenTofuImage,
                backend,
                inputs.Variables,
                env,
                secretEnv),
            Proxmox = new ProxmoxContext(endpoint, connection.VerifyTls, connection.CaCertificate?.Key, guest.Host, guest.Vmid, guest.Id, ProvisionInputs.ExpectedIpv4(guest),
                guest.IsContainer ? "lxc" : null, inputs.Installation.ReservedVmids),
            Secrets = secrets,
            RequestedSecretVersions = requestedVersions,
            Verify = inputs.Recipe.Verify,
            CodeDigest = ReleaseBinding.Of(repository, guest).Digest,
            ReviewedPaths = inputs.Code.Paths,
            CallsNoModules = inputs.Code.CallsNoModules,
            RootDiskBytes = inputs.Recipe.GuestFilesystem is null ? null
                : checked(guest.Json["hardware"]!["disks"]![0]!["size"]!.GetValue<long>() * 1024 * 1024 * 1024),
            DiskReporter = installReporter ? new DiskReporterContext(
                $"{reporter.PublicUrl.TrimEnd('/')}/api/guests/{resource.Id}/disk-usage",
                nativeReporter ? null : reporter.BinaryUrl,
                nativeReporter ? null : reporter.BinarySha256.ToLowerInvariant(),
                reporterInterval,
                string.IsNullOrWhiteSpace(reporter.CaCertificatePem) ? null : reporter.CaCertificatePem,
                nativeReporter ? "managed-nixos-v1" : "fhs-download-v1",
                nativeReporter ? nativeImageHash!.ToLowerInvariant() : null) : null,
        };

        var run = NewRun(resource, retire ? Operations.Retire : Operations.Provision, revision, reason, context);
        run.InputsHash = retire ? RetireHash(inputs.Hash) : inputs.Hash;
        var steps = retire
            ? new[] { StepNames.Plan, StepNames.Approval, StepNames.Unprotect, StepNames.Apply, StepNames.VerifyAbsent }
            : context.RootDiskBytes is not null
                ? [StepNames.Plan, StepNames.Approval, StepNames.Unprotect, StepNames.Apply, StepNames.Verify, StepNames.GrowRoot, StepNames.VerifyRoot]
                : [StepNames.Plan, StepNames.Approval, StepNames.Unprotect, StepNames.Apply, StepNames.Verify];
        if (context.ManagedWorker is not null && retire)
            // Another worker destroys the VM; this one stops taking work first and loses its
            // enrollment only once the VM is verified absent.
            steps = [StepNames.Plan, StepNames.Approval, StepNames.DrainWorker, StepNames.Unprotect, StepNames.Apply,
                StepNames.VerifyAbsent, StepNames.RevokeWorker];
        else if (context.ManagedWorker is not null)
            // A worker's VM is never replaced, so its provision runs never touch protection.
            steps = [.. steps.Where(s => s != StepNames.Unprotect), StepNames.PrepareWorker, StepNames.InstallWorker, StepNames.WaitWorker];
        if (update is not null)
        {
            var beforeRoot = Array.IndexOf(steps, StepNames.GrowRoot);
            if (context.ManagedWorker is null || resource.ManagedWorker?.ReadyAt is null || beforeRoot < 0)
                throw new RunException("An in-place OS update requires an enrolled, Ready NixOS worker with its existing state disk.");
            steps = [.. steps.Take(beforeRoot), StepNames.UpdateWorkerOs, StepNames.VerifyWorkerOs, .. steps.Skip(beforeRoot), StepNames.WaitUpdatedWorker];
        }
        AddSteps(run, steps);
        if (update is not null)
            run.Steps.Single(s => s.Name == StepNames.UpdateWorkerOs).Detail =
                $"Switch this existing VM to {update.SystemPath}; approved closure archive SHA-256 {update.ArchiveSha256}. Preserve identity/state and retain the previous system generation.";
        if (resource.ManagedWorker?.ReadyAt is not null && !retire)
            foreach (var step in run.Steps.Where(s => s.Name is StepNames.PrepareWorker or StepNames.InstallWorker or StepNames.WaitWorker))
            {
                step.Status = StepStatuses.Skipped;
                step.Detail = "This generation already completed authenticated bootstrap.";
            }
        if (context.DiskReporter is { } setup)
        {
            run.Steps.Add(new Step
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                Ordinal = run.Steps.Count,
                Name = StepNames.InstallDiskReporter,
                Kind = StepKinds.Agent,
                Detail = setup.InstallationMode == "managed-nixos-v1"
                    ? $"Configure image-native disk reporter (image SHA-256 {setup.ImageSha256}); report every {setup.IntervalSeconds}s."
                    : $"Install disk reporter SHA-256 {setup.BinarySha256}; report every {setup.IntervalSeconds}s.",
            });
        }
        if (context.Verify == "none" && !installReporter)
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
    /// A cluster run: inspect the members, plan, approval, apply, then what the plan decided about
    /// members and block lists. Verification runs on every run, including one with no changes.
    /// </summary>
    public Run Cluster(LabRepository repository, ClusterDefinition cluster, Resource resource, DefinitionRevision revision, string reason)
    {
        var inputs = ClusterInputs.Build(repository, cluster, revision.Commit);
        var policy = PolicyResolver.For(repository, cluster);
        var context = ClusterBase(repository, inputs, revision) with
        {
            Policy = new PolicyContext(policy.Approval.Value, policy.Approval.SetBy, policy.Destructive.Value, policy.Destructive.SetBy),
            // The password reaches OpenTofu in the environment only, so it enters neither the saved
            // plan nor the state. Trust keeps code that an operator has not seen from receiving it.
            OpenTofu = new OpenTofuContext(
                cluster.Stack,
                inputs.Recipe.Root,
                inputs.Recipe.Image ?? options.Value.Tools.OpenTofuImage,
                BackendContext.BuiltIn,
                inputs.Variables,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["TECHNITIUM_USERNAME"] = "admin" },
                new Dictionary<string, string>(StringComparer.Ordinal) { ["TECHNITIUM_PASSWORD"] = SecretNames.ClusterPassword }),
        };
        context.Cluster!.Input["formed"] = resource.BoundPrimary is not null;
        var run = NewRun(resource, Operations.Cluster, revision, reason, context);
        run.InputsHash = inputs.Hash;
        AddSteps(run, [StepNames.InspectCluster, StepNames.Plan, StepNames.Approval, StepNames.DetachMembers, StepNames.Apply,
            StepNames.ReconcileCluster, StepNames.RefreshBlockLists, StepNames.VerifyCluster]);
        return run;
    }

    /// <summary>What a background check of a cluster's members is built from.</summary>
    public static RunContext ClusterObservation(LabRepository repository, ClusterDefinition cluster, DefinitionRevision revision) =>
        ClusterBase(repository, ClusterInputs.Build(repository, cluster, revision.Commit), revision);

    /// <summary>What reading the members' certificates is built from: no password, so no trust is needed.</summary>
    public static RunContext ClusterProbe(LabRepository repository, ClusterDefinition cluster, DefinitionRevision revision)
    {
        var context = ClusterObservation(repository, cluster, revision);
        return context with
        {
            Cluster = context.Cluster! with { Probe = true },
            Secrets = [],
            RequestedSecretVersions = [],
            CodeDigest = null,
            ReviewedPaths = null,
        };
    }

    private static RunContext ClusterBase(LabRepository repository, ClusterInputs inputs, DefinitionRevision revision)
    {
        var cluster = inputs.Cluster;
        var labels = PolicyResolver.AgentLabels(repository, cluster);
        var settings = cluster.Json["settings"]!;
        var blocking = settings["blocking"];
        var variables = inputs.Variables["cluster"]!["settings"]!;
        var requestedVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        if (cluster.Credentials.Version is { } version)
        {
            requestedVersions[SecretNames.ClusterPassword] = version;
        }

        var input = new JsonObject
        {
            ["cluster"] = cluster.Address,
            ["clusterDomain"] = cluster.ClusterDomain,
            ["minimumVersion"] = ClusterInputs.MinimumVersion,
            ["members"] = new JsonArray(inputs.Members.Select(m => (JsonNode?)new JsonObject
            {
                ["guest"] = m.Guest,
                ["role"] = m.Role,
                ["address"] = m.Address,
                ["endpoint"] = m.Endpoint,
            }).ToArray()),
            ["settings"] = new JsonObject
            {
                ["enableBlocking"] = variables["blocking"]!["enabled"]!.DeepClone(),
                ["blockingType"] = variables["blocking"]!["type"]!.DeepClone(),
                ["blockListUrls"] = variables["blocking"]!["lists"]!.DeepClone(),
                ["blockListUpdateIntervalHours"] = variables["blocking"]!["updateIntervalHours"]!.DeepClone(),
                ["recursion"] = variables["recursion"]!["policy"]!.DeepClone(),
                ["recursionNetworkACL"] = variables["recursion"]!["networks"]!.DeepClone(),
            },
            ["blocked"] = variables["blocking"]!["blocked"]!.DeepClone(),
            ["allowed"] = variables["blocking"]!["allowed"]!.DeepClone(),
            ["listProbes"] = blocking?["listProbes"]?.DeepClone() ?? new JsonArray(),
            ["forwardZones"] = new JsonArray(ClusterInputs.ForwardZones(settings).Select(z => (JsonNode?)z.Zone).ToArray()),
            ["control"] = blocking?["control"]?.GetValue<string>(),
        };
        return new RunContext
        {
            Address = cluster.Address,
            Commit = revision.Commit,
            AgentLabels = labels.Value,
            AgentLabelsSetBy = labels.SetBy,
            Cluster = new ClusterContext(
                cluster.Type,
                inputs.Members.Single(m => m.Role == ClusterRoles.Primary).Guest,
                cluster.ClusterDomain,
                inputs.Members.Select(m => new ClusterMemberContext(m.Guest, m.Role, m.Address)).ToList(),
                inputs.Endpoints,
                inputs.CodeDigest,
                input,
                Canonical.Hash(variables["blocking"]!["lists"])),
            CodeDigest = inputs.CodeDigest,
            ReviewedPaths = inputs.Code.Paths,
            CallsNoModules = inputs.Code.CallsNoModules,
            Secrets = new Dictionary<string, string>(StringComparer.Ordinal) { [SecretNames.ClusterPassword] = cluster.Credentials.Key },
            RequestedSecretVersions = requestedVersions,
        };
    }

    /// <summary>
    /// A configure run: one action of the guest's setup recipe. It reaches the guest over SSH, so
    /// it holds no Proxmox credentials. The installation's endpoint stays in its context because
    /// trust in that installation still decides whether its guests are worked on.
    /// </summary>
    public Run Configure(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision,
        string actionId, string reason, GuestConfiguration? configuration, IReadOnlyList<string>? changedFiles)
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

        Bind(SecretNames.AnsibleSshKey, inputs.SshKey);
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

    /// <summary>Verify an explicitly removed worker; never plans, unprotects or destroys infrastructure.</summary>
    public Run VerifyWorkerRetirement(LabRepository repository, InstallationDefinition installation, Resource resource,
        DefinitionRevision revision, Run original, string reason)
    {
        var binding = resource.ManagedWorker!;
        if (!installation.Hosts.TryGetValue(binding.Host, out var host))
            throw new RunException("Restore the worker's original Proxmox host before verifying retirement.");
        var previous = RunContext.Parse(original.Context);
        var endpoint = ProvisionInputs.EndpointFor(installation, host);
        if (endpoint != previous.Proxmox.Endpoint || previous.ManagedWorker?.Generation != binding.Generation)
            throw new RunException("The worker's original endpoint or generation is unavailable; retirement cannot be inferred from another installation.");
        // A read-only check with the installation's observation credentials.
        var context = Observation(repository, installation, revision);
        var run = NewRun(resource, Operations.Retire, revision, reason, context with
        {
            Address = resource.Address,
            HostKey = $"{installation.Address}/{host.Id}",
            HostLimit = host.ConcurrentOperations,
            Proxmox = context.Proxmox with { Endpoint = endpoint, Node = binding.Host, Vmid = binding.Vmid, Name = previous.Proxmox.Name },
            ManagedWorker = previous.ManagedWorker,
        });
        run.InputsHash = RetireHash(resource.DesiredInputsHash ?? InstallationHash(installation));
        AddSteps(run, [StepNames.VerifyAbsent]);
        return run;
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
                Kind = names[i] is StepNames.Approval or StepNames.OutcomeApproval ? StepKinds.Approval : StepKinds.Agent,
            });
        }
    }
}
