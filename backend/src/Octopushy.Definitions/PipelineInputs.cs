using System.Text.Json.Nodes;

namespace Octopushy.Definitions;

/// <summary>A stage and the recipe it runs.</summary>
public sealed record StageInputs(StageDefinition Stage, RecipeDefinition Recipe);

/// <summary>
/// Everything a service's run is built from, and a hash of it that changes exactly when a run could
/// differ: the pipeline, the recipes of its stages with their files, the service's own files, and
/// how its guest is reached. Its policy and labels are not part of it, and neither is the guest's
/// hardware or setup: a service runs again when something about the service changes.
/// </summary>
public sealed record PipelineInputs(ServiceDefinition Service, GuestDefinition Guest, IReadOnlyList<StageInputs> Stages, string Hash)
{
    /// <summary>Builds the inputs of a valid service; the repository must have validated it.</summary>
    public static PipelineInputs Build(LabRepository repository, ServiceDefinition service)
    {
        var guest = repository.Guest(service.GuestAddress)!;
        var stages = service.Stages.Select(s => new StageInputs(s, repository.Recipes[s.Recipe])).ToList();
        var pipeline = service.Json.DeepClone().AsObject();
        foreach (var field in new[] { "description", "policy", "execution" })
        {
            pipeline.Remove(field);
        }

        var hashed = new JsonObject
        {
            ["pipeline"] = pipeline,
            ["serviceFiles"] = ProvisionInputs.TreeHash(repository.Root, service.Directory, "pipeline.yaml"),
            ["access"] = guest.Json["access"]?.DeepClone(),
            ["address"] = ConfigureInputs.AddressOf(guest),
            ["recipes"] = new JsonArray(stages.Select(s => (JsonNode?)new JsonObject
            {
                ["recipe"] = s.Recipe.Json.DeepClone(),
                ["recipeFiles"] = ProvisionInputs.TreeHash(repository.Root, Path.GetDirectoryName(s.Recipe.File)!),
                ["rootFiles"] = ProvisionInputs.TreeHash(repository.Root, s.Recipe.Root),
            }).ToArray()),
        };
        return new PipelineInputs(service, guest, stages, Canonical.Hash(hashed));
    }
}
