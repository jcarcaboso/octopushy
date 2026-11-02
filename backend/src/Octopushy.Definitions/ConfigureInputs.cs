using System.Text.Json.Nodes;

namespace Octopushy.Definitions;

/// <summary>
/// Everything a configure run on a guest or a service is built from, and a hash of it that changes
/// exactly when a run could differ: its setup or deploy section, the guest's access, the recipe, its
/// files and project, and the resource's own files. A guest's hardware and network are not part of it,
/// and neither are its services.
/// </summary>
public sealed record ConfigureInputs(
    GuestDefinition Guest,
    ServiceDefinition? Service,
    RecipeDefinition Recipe,
    ConfigureRecipe Configure,
    string Address,
    int Port,
    string User,
    StoreReference SshKey,
    StoreReference HostKey,
    JsonObject Vars,
    IReadOnlyDictionary<string, StoreReference> SecretVars,
    IReadOnlyDictionary<string, StoreReference> SecretFiles,
    /// <summary>The resource's own folder, which a run's <c>octopushy.guest_directory</c> names.</summary>
    string Directory,
    string Hash,
    /// <summary>What a grant of a protected secret covers: the code, and everything that decides who ends up with the secret.</summary>
    string CodeDigest,
    JsonObject Execution,
    /// <summary>The folders that digest covers: all a run holding a protected secret receives.</summary>
    IReadOnlyList<string> ReviewedPaths)
{
    /// <summary>Fields of <c>access</c> that only configure runs use.</summary>
    public static readonly string[] AccessOnly = ["user", "port", "hostKey"];

    /// <summary>Where a configure run connects: the declared address, or the guest's own.</summary>
    public static string? AddressOf(GuestDefinition guest) =>
        guest.Json["access"]?["address"]?.GetValue<string>() ?? ProvisionInputs.ExpectedIpv4(guest);

    /// <summary>A store reference in <c>access</c>.</summary>
    public static StoreReference? Reference(GuestDefinition guest, string field) =>
        guest.Json["access"]?[field] is JsonObject reference ? Parse(reference) : null;

    /// <summary>The <c>secrets</c> or <c>secretFiles</c> of a guest's <c>setup</c> or a service's <c>deploy</c>.</summary>
    public static IReadOnlyDictionary<string, StoreReference> Secrets(JsonNode? binding, string field) =>
        (binding?[field] as JsonObject)?.ToDictionary(p => p.Key, p => Parse(p.Value!), StringComparer.Ordinal) ?? [];

    private static StoreReference Parse(JsonNode node) =>
        new(node["store"]!.GetValue<string>(), node["version"]?.GetValue<int>());

    /// <summary>Builds the inputs for a valid guest with a setup recipe; the repository must have validated it.</summary>
    public static ConfigureInputs Build(LabRepository repository, GuestDefinition guest)
    {
        var setup = guest.Json["setup"]!.DeepClone().AsObject();
        // Reporter settings have their own place in the provisioning hash.
        setup.Remove("diskReporter");
        var hashed = new JsonObject
        {
            ["setup"] = setup,
            // A service's files are the service's: changing one does not configure the guest again.
            ["guestFiles"] = ProvisionInputs.TreeHash(repository.Root, guest.Directory, "guest.yaml", "services"),
        };
        return Build(repository, guest, null, repository.Recipes[guest.SetupRecipe!], guest.Json["setup"]!, guest.Directory, hashed,
            PolicyResolver.AgentLabels(repository, guest).Value);
    }

    /// <summary>Builds the inputs for a valid service; the repository must have validated it.</summary>
    public static ConfigureInputs Build(LabRepository repository, ServiceDefinition service)
    {
        var definition = service.Json.DeepClone().AsObject();
        definition.Remove("description");
        definition.Remove("policy");
        definition.Remove("execution");
        var hashed = new JsonObject
        {
            ["service"] = definition,
            ["serviceFiles"] = ProvisionInputs.TreeHash(repository.Root, service.Directory, "service.yaml"),
        };
        return Build(repository, repository.Guest(service.Guest)!, service, repository.Recipes[service.Recipe], service.Json["deploy"]!, service.Directory, hashed,
            PolicyResolver.AgentLabels(repository, service).Value);
    }

    private static ConfigureInputs Build(LabRepository repository, GuestDefinition guest, ServiceDefinition? service, RecipeDefinition recipe, JsonNode binding,
        string directory, JsonObject hashed, IReadOnlyList<string> agentLabels)
    {
        var folder = Path.GetDirectoryName(recipe.File)!;
        var secretVars = Secrets(binding, "secrets");
        var secretFiles = Secrets(binding, "secretFiles");
        hashed["access"] = guest.Json["access"]?.DeepClone();
        hashed["address"] = AddressOf(guest);
        hashed["recipe"] = recipe.Json.DeepClone();
        hashed["recipeFiles"] = ProvisionInputs.TreeHash(repository.Root, folder);
        hashed["rootFiles"] = ProvisionInputs.TreeHash(repository.Root, recipe.Root);

        var vars = binding["inputs"]?.DeepClone().AsObject() ?? [];
        var port = guest.Json["access"]?["port"]?.GetValue<int>() ?? 22;
        var user = guest.Json["access"]!["user"]!.GetValue<string>();
        var sshKey = Reference(guest, "sshKey")!;
        var hostKey = Reference(guest, "hostKey")!;
        var execution = new JsonObject
        {
            ["address"] = AddressOf(guest),
            ["port"] = port,
            ["user"] = user,
            ["sshKey"] = sshKey.Key,
            ["hostKey"] = hostKey.Key,
            ["inputs"] = vars.DeepClone(),
            ["secrets"] = new JsonObject(secretVars.Concat(secretFiles).OrderBy(s => s.Key, StringComparer.Ordinal)
                .Select(s => KeyValuePair.Create(s.Key, (JsonNode?)s.Value.Key))),
            ["ports"] = service?.Ports,
            ["agentLabels"] = new JsonArray(agentLabels.Select(l => (JsonNode?)l).ToArray()),
        };
        var code = new JsonObject
        {
            ["recipe"] = recipe.Json.DeepClone(),
            ["recipeFiles"] = ProvisionInputs.TreeHash(repository.Root, folder),
            ["rootFiles"] = ProvisionInputs.TreeHash(repository.Root, recipe.Root),
            // A run holding a protected secret receives this whole folder, so all of it counts here.
            ["ownFiles"] = ProvisionInputs.TreeHash(repository.Root, directory, service is null ? "guest.yaml" : "service.yaml"),
        };
        return new ConfigureInputs(
            guest,
            service,
            recipe,
            recipe.Configure!,
            AddressOf(guest)!,
            port,
            user,
            sshKey,
            hostKey,
            vars,
            secretVars,
            secretFiles,
            directory,
            Canonical.Hash(hashed),
            Canonical.Hash(new JsonObject { ["code"] = code, ["execution"] = execution.DeepClone() }),
            execution,
            [folder, recipe.Root, directory]);
    }
}
