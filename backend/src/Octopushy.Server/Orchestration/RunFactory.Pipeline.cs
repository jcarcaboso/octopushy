using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Store;

namespace Octopushy.Server.Orchestration;

/// <summary>Builds pipeline runs.</summary>
public sealed partial class RunFactory
{
    /// <summary>
    /// A service's run: a plan or preview of every stage, one approval, then every stage applied in
    /// order. An Ansible stage is the onChange action of its recipe, on the guest's connection. An
    /// OpenTofu stage has its inputs and a state of its own, and no credentials of any kind.
    /// </summary>
    public Run Pipeline(LabRepository repository, ServiceDefinition service, Resource resource, Resource guestResource, DefinitionRevision revision, string reason)
    {
        Managed(repository, repository.Guest(service.GuestAddress)?.InstallationAddress ?? string.Join('/', service.GuestAddress.Split('/').Take(2)));
        var inputs = PipelineInputs.Build(repository, service);
        var guest = inputs.Guest;
        if (guest.Retired)
            throw new RunException("A retired guest gets no new services.");
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        var requestedVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        var stages = new List<StageContext>();
        foreach (var (stage, recipe) in inputs.Stages.Select(s => (s.Stage, s.Recipe)))
        {
            void Bind(string role, StoreReference reference)
            {
                var scoped = RunContext.StageRole(stage.Name, role);
                secrets[scoped] = reference.Key;
                if (reference.Version is { } version)
                    requestedVersions[scoped] = version;
            }

            if (recipe.Configure is not { } configure)
            {
                stages.Add(new StageContext(stage.Name, recipe.Id, new OpenTofuContext(
                    $"{service.Address.Replace('/', '.')}.{stage.Name}",
                    recipe.Root,
                    recipe.Image ?? options.Value.Tools.OpenTofuImage,
                    stage.Inputs.DeepClone().AsObject(),
                    [],
                    []), null));
                continue;
            }

            if (ConfigureInputs.Reference(guest, "sshKey") is { } declared)
                Bind(SecretNames.AnsibleSshKey, declared);
            else
                secrets[RunContext.StageRole(stage.Name, SecretNames.AnsibleSshKey)] = GuestAccessCredentials.Key(guestResource.Id);
            Bind(SecretNames.AnsibleHostKey, ConfigureInputs.Reference(guest, "hostKey")!);
            foreach (var (name, reference) in stage.Secrets)
            {
                Bind(SecretNames.AnsibleVar(name), reference);
            }

            foreach (var (name, reference) in stage.SecretFiles)
            {
                Bind(SecretNames.AnsibleFile(name), reference);
            }

            var action = configure.OnChange!;
            stages.Add(new StageContext(stage.Name, recipe.Id, null, new AnsibleContext(
                recipe.Id,
                action.Id,
                recipe.Root,
                action.Playbook,
                action.Prepare,
                action.UsesAppliedSnapshot,
                action.Destructive,
                action.TimeoutMinutes,
                recipe.Image ?? options.Value.Tools.AnsibleImage,
                guest.Id,
                service.Directory,
                ConfigureInputs.AddressOf(guest)!,
                guest.Json["access"]?["port"]?.GetValue<int>() ?? 22,
                guest.Json["access"]!["user"]!.GetValue<string>(),
                stage.Inputs.DeepClone().AsObject(),
                stage.Secrets.Keys.ToList(),
                stage.SecretFiles.Keys.ToList(),
                configure.Outcomes.ToDictionary(o => o.Key, o => new AnsibleOutcome(o.Value.Description, o.Value.ApproveVars, o.Value.Result), StringComparer.Ordinal),
                null)));
        }

        var installation = repository.Installation(guest.InstallationAddress)!;
        var policy = PolicyResolver.For(repository, guest, service);
        var labels = PolicyResolver.AgentLabels(repository, guest, service);
        var run = NewRun(resource, Operations.Pipeline, revision, reason, new RunContext
        {
            Address = service.Address,
            Commit = revision.Commit,
            AgentLabels = labels.Value,
            AgentLabelsSetBy = labels.SetBy,
            Policy = new PolicyContext(policy.Approval.Value, policy.Approval.SetBy, policy.Destructive.Value, policy.Destructive.SetBy, policy.DestructiveExplicit),
            // No token goes with this. Trust in the installation still decides whether its guests are worked on.
            Proxmox = new ProxmoxContext(ProvisionInputs.EndpointFor(installation, installation.Hosts[guest.Host]), installation.Connection.VerifyTls, null,
                guest.Host, guest.Vmid, guest.Id, null),
            Secrets = secrets,
            RequestedSecretVersions = requestedVersions,
            Stages = stages,
            GuestResourceId = guestResource.Id,
        });
        run.InputsHash = inputs.Hash;

        void Add(string name, string? stage, string kind) => run.Steps.Add(new Step
        {
            Id = Guid.CreateVersion7(),
            RunId = run.Id,
            Ordinal = run.Steps.Count,
            Name = name,
            Stage = stage,
            Kind = kind,
        });

        foreach (var stage in stages)
        {
            Add(stage.Ansible is null ? StepNames.Plan : StepNames.Preview, stage.Name, StepKinds.Agent);
        }

        Add(StepNames.Approval, null, StepKinds.Approval);
        foreach (var stage in stages)
        {
            Add(stage.Ansible is null ? StepNames.Apply : StepNames.Playbook, stage.Name, StepKinds.Agent);
        }

        return run;
    }
}
