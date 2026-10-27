using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Options;

namespace Octopushy.Server.Orchestration;

/// <summary>Builds runs and their steps, and observations, from a revision's definitions.</summary>
public sealed class RunFactory(IOptions<OctopushyOptions> options, TimeProvider clock)
{
    /// <summary>A provision run (plan, approval, apply, verify) or, for a retired guest, a retire run.</summary>
    public Run Guest(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision, string reason) =>
        CreateGuest(repository, guest, resource, revision, reason, null);

    public Run UpdateWorker(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision, string reason, WorkerUpdateContext update) =>
        CreateGuest(repository, guest, resource, revision, reason, update);

    private Run CreateGuest(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision, string reason, WorkerUpdateContext? update)
    {
        if (resource.ManagedWorker is { } bound && (guest.Retired || guest.ManagedWorker?.Generation != bound.Generation
            || guest.Host != bound.Host || guest.Vmid != bound.Vmid || guest.InstallationAddress != bound.Installation))
            throw new RunException("Managed workers cannot be retired, replaced, moved or have their worker role removed; retain the existing binding.");
        if (guest.ManagedWorker is not null && guest.Retired)
            throw new RunException("Managed worker retirement is not supported.");
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
        foreach (var (name, value) in inputs.Backend.Env)
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

        var config = inputs.Backend.Config.ToDictionary(
            p => p.Key,
            p => p.Value is JsonValue v && v.TryGetValue<string>(out var text) ? JsonValue.Create(text.Replace("{stack}", guest.Stack)) : p.Value?.DeepClone(),
            StringComparer.Ordinal);

        var retire = guest.Retired;
        var reporter = options.Value.DiskReporter;
        reporter.Validate();
        var guestReporter = guest.Json["setup"]?["diskReporter"];
        var installReporter = !retire && reporter.Enabled && guestReporter?["enabled"]?.GetValue<bool>() != false;
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
                guest.ManagedWorker is null ? policy.Approval.SetBy : "managed-worker safety", policy.Destructive.Value, policy.Destructive.SetBy),
            ManagedWorker = guest.ManagedWorker is { } worker ? new ManagedWorkerContext(worker.Generation, worker.Labels,
                worker.MaxConcurrency, ManagedWorkerSafety.PublicUrl(options.Value.AgentChannel.PublicUrl)) : null,
            WorkerUpdate = update,
            OpenTofu = new OpenTofuContext(
                guest.Stack,
                inputs.Recipe.Root,
                inputs.Recipe.Image ?? options.Value.Tools.OpenTofuImage,
                new BackendContext(inputs.Backend.Type, config, inputs.Backend.Workspace?.Replace("{stack}", guest.Stack)),
                inputs.Variables,
                env,
                secretEnv),
            Proxmox = new ProxmoxContext(endpoint, connection.VerifyTls, connection.CaCertificate?.Key, guest.Host, guest.Vmid, guest.Id, ProvisionInputs.ExpectedIpv4(guest)),
            Secrets = secrets,
            RequestedSecretVersions = requestedVersions,
            Verify = inputs.Recipe.Verify,
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
                ? [StepNames.Plan, StepNames.Approval, StepNames.Apply, StepNames.Verify, StepNames.GrowRoot, StepNames.VerifyRoot]
                : [StepNames.Plan, StepNames.Approval, StepNames.Apply, StepNames.Verify];
        if (context.ManagedWorker is not null)
            steps = [.. steps, StepNames.PrepareWorker, StepNames.InstallWorker, StepNames.WaitWorker];
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
        if (resource.ManagedWorker?.ReadyAt is not null)
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
                Kind = names[i] == StepNames.Approval ? StepKinds.Approval : StepKinds.Agent,
            });
        }
    }
}
