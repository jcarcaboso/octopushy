using System.Text.Json.Nodes;

namespace Octopushy.Definitions;

/// <summary>
/// Everything a service's run is built from, and a hash of it that changes exactly when a run could
/// differ: the service's definition, the workflow it resolved to with its version, its folder and the
/// roots of its stages, the service's own files, and how its guest is reached. Its policy and labels
/// are not part of it, and neither is the guest's hardware or setup: a service runs again when
/// something about the service changes.
/// </summary>
public sealed record PipelineInputs(ServiceDefinition Service, GuestDefinition Guest, WorkflowDefinition Workflow, IReadOnlyList<StageDefinition> Stages, string Hash)
{
    /// <summary>Service fields that change nothing a run does. The reference is replaced by what it resolved to.</summary>
    private static readonly string[] NotRun = ["description", "policy", "execution", "workflow"];

    /// <summary>Builds the inputs of a valid service; the repository must have validated it.</summary>
    public static PipelineInputs Build(LabRepository repository, ServiceDefinition service)
    {
        var guest = repository.Guest(service.GuestAddress)!;
        var workflow = service.Workflow!;
        var definition = service.Json.DeepClone().AsObject();
        foreach (var field in NotRun)
        {
            definition.Remove(field);
        }

        var hashed = new JsonObject
        {
            ["service"] = definition,
            ["serviceFiles"] = ProvisionInputs.TreeHash(repository.Root, service.Directory, "service.yaml"),
            ["access"] = guest.Json["access"]?.DeepClone(),
            ["address"] = ConfigureInputs.AddressOf(guest),
            ["workflow"] = ProvisionInputs.WorkflowHash(workflow, workflow.Roots),
        };
        return new PipelineInputs(service, guest, workflow, service.Stages, Canonical.Hash(hashed));
    }
}
