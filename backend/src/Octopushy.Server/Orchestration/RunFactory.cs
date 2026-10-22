using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Options;

namespace Octopushy.Server.Orchestration;

/// <summary>Builds runs and their steps from a revision's definitions.</summary>
public sealed class RunFactory(IOptions<OctopushyOptions> options, TimeProvider clock)
{
    /// <summary>A provision run (plan, approval, apply, verify) or, for a retired guest, a retire run.</summary>
    public Run Guest(LabRepository repository, GuestDefinition guest, Resource resource, DefinitionRevision revision, string reason)
    {
        var inputs = ProvisionInputs.Build(repository, guest, revision.Commit);
        var policy = PolicyResolver.For(repository, guest);
        var labels = PolicyResolver.AgentLabels(repository, guest);
        var connection = inputs.Installation.Connection;
        var endpoint = ProvisionInputs.EndpointFor(inputs.Installation, inputs.Host);

        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SecretNames.ProxmoxToken] = connection.Token.Key,
        };
        var requestedVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        if (connection.Token.Version is { } tokenVersion)
        {
            requestedVersions[SecretNames.ProxmoxToken] = tokenVersion;
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
        var context = new RunContext
        {
            Address = guest.Address,
            Commit = revision.Commit,
            AgentLabels = labels.Value,
            AgentLabelsSetBy = labels.SetBy,
            HostKey = $"{inputs.Installation.Address}/{inputs.Host.Id}",
            HostLimit = inputs.Host.ConcurrentOperations,
            Policy = new PolicyContext(policy.Approval.Value, policy.Approval.SetBy, policy.Destructive.Value, policy.Destructive.SetBy),
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
        };

        var run = NewRun(resource, retire ? Operations.Retire : Operations.Provision, revision, reason, context);
        run.InputsHash = retire ? RetireHash(inputs.Hash) : inputs.Hash;
        var steps = retire
            ? new[] { StepNames.Plan, StepNames.Approval, StepNames.Unprotect, StepNames.Apply, StepNames.VerifyAbsent }
            : context.RootDiskBytes is not null
                ? [StepNames.Plan, StepNames.Approval, StepNames.Apply, StepNames.Verify, StepNames.GrowRoot, StepNames.VerifyRoot]
                : [StepNames.Plan, StepNames.Approval, StepNames.Apply, StepNames.Verify];
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

    /// <summary>An observe run: the installation's hosts and VMs as its Proxmox API reports them.</summary>
    public Run Observe(LabRepository repository, InstallationDefinition installation, Resource resource, DefinitionRevision revision, string reason)
    {
        var labels = PolicyResolver.AgentLabels(repository, installation);
        var connection = installation.Connection;
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SecretNames.ProxmoxToken] = connection.Token.Key,
        };
        var requestedVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        if (connection.Token.Version is { } tokenVersion)
        {
            requestedVersions[SecretNames.ProxmoxToken] = tokenVersion;
        }
        if (connection.CaCertificate is { } ca)
        {
            secrets[SecretNames.ProxmoxCa] = ca.Key;
            if (ca.Version is { } caVersion)
            {
                requestedVersions[SecretNames.ProxmoxCa] = caVersion;
            }
        }

        var context = new RunContext
        {
            Address = installation.Address,
            Commit = revision.Commit,
            AgentLabels = labels.Value,
            AgentLabelsSetBy = labels.SetBy,
            Proxmox = new ProxmoxContext(connection.Endpoint, connection.VerifyTls, connection.CaCertificate?.Key, null, null, null, null),
            Secrets = secrets,
            RequestedSecretVersions = requestedVersions,
        };
        var run = NewRun(resource, Operations.Observe, revision, reason, context);
        run.InputsHash = InstallationHash(installation);
        AddSteps(run, [StepNames.Observe]);
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
