using System.Text.Json.Nodes;

namespace Octopushy.Definitions;

/// <summary>
/// Everything a guest's configure runs are built from, and a hash of it that changes exactly
/// when a run could differ: the guest's setup and access, the recipe, its files and project, and
/// the guest's own files. A guest's hardware and network are not part of it.
/// </summary>
public sealed record ConfigureInputs(
    GuestDefinition Guest,
    RecipeDefinition Recipe,
    ConfigureRecipe Configure,
    string Address,
    int Port,
    string User,
    /// <summary>A key the guest names itself; null means Octopushy's own, see <see cref="ManagedAccess"/>.</summary>
    StoreReference? SshKey,
    StoreReference HostKey,
    JsonObject Vars,
    IReadOnlyDictionary<string, StoreReference> SecretVars,
    IReadOnlyDictionary<string, StoreReference> SecretFiles,
    string Hash)
{
    /// <summary>Fields of <c>access</c> that only configure runs use. None of them reaches OpenTofu.</summary>
    public static readonly string[] AccessOnly = ["address", "port", "user", "sshKey", "hostKey"];

    /// <summary>Where a configure run connects: the declared address, or the guest's own.</summary>
    public static string? AddressOf(GuestDefinition guest) =>
        guest.Json["access"]?["address"]?.GetValue<string>() ?? ProvisionInputs.ExpectedIpv4(guest);

    /// <summary>A store reference in <c>access</c>.</summary>
    public static StoreReference? Reference(GuestDefinition guest, string field) =>
        guest.Json["access"]?[field] is JsonObject reference ? Parse(reference) : null;

    /// <summary>A guest's <c>setup.secrets</c> or <c>setup.secretFiles</c>.</summary>
    public static IReadOnlyDictionary<string, StoreReference> Secrets(GuestDefinition guest, string field) =>
        (guest.Json["setup"]?[field] as JsonObject)?.ToDictionary(p => p.Key, p => Parse(p.Value!), StringComparer.Ordinal) ?? [];

    private static StoreReference Parse(JsonNode node) =>
        new(node["store"]!.GetValue<string>(), node["version"]?.GetValue<int>());

    /// <summary>Builds the inputs for a valid guest with a setup recipe; the repository must have validated it.</summary>
    public static ConfigureInputs Build(LabRepository repository, GuestDefinition guest)
    {
        var recipe = repository.Recipes[guest.SetupRecipe!];
        var setup = guest.Json["setup"]!.DeepClone().AsObject();
        // Reporter settings have their own place in the provisioning hash, and a schedule says
        // when an action runs, not what it does, so changing one configures nothing.
        setup.Remove("diskReporter");
        setup.Remove("schedules");
        var hashed = new JsonObject
        {
            ["setup"] = setup,
            ["access"] = guest.Json["access"]?.DeepClone(),
            ["address"] = AddressOf(guest),
            ["recipe"] = recipe.Json.DeepClone(),
            ["recipeFiles"] = ProvisionInputs.TreeHash(repository.Root, Path.GetDirectoryName(recipe.File)!),
            ["rootFiles"] = ProvisionInputs.TreeHash(repository.Root, recipe.Root),
            ["guestFiles"] = ProvisionInputs.TreeHash(repository.Root, guest.Directory, "guest.yaml"),
        };
        return new ConfigureInputs(
            guest,
            recipe,
            recipe.Configure!,
            AddressOf(guest)!,
            guest.Json["access"]?["port"]?.GetValue<int>() ?? 22,
            guest.Json["access"]!["user"]!.GetValue<string>(),
            Reference(guest, "sshKey"),
            Reference(guest, "hostKey")!,
            setup["inputs"]?.DeepClone().AsObject() ?? [],
            Secrets(guest, "secrets"),
            Secrets(guest, "secretFiles"),
            Canonical.Hash(hashed));
    }
}
