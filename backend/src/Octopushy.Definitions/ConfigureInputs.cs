using System.Text.Json.Nodes;

namespace Octopushy.Definitions;

/// <summary>
/// Everything a guest's configure runs are built from, and a hash of it that changes exactly
/// when a run could differ: the guest's inputs, secrets and access, the workflow it resolved to
/// with its version, its folder and the root of its setup stage, and the guest's own files. A
/// guest's hardware and network are not part of it.
/// </summary>
public sealed record ConfigureInputs(
    GuestDefinition Guest,
    WorkflowDefinition Workflow,
    WorkflowStage Stage,
    ConfigureActions Configure,
    string Address,
    int Port,
    string User,
    /// <summary>A key the guest names itself; null means Octopushy's own, see <see cref="ManagedAccess"/>.</summary>
    StoreReference? SshKey,
    /// <summary>The pin the guest names itself; null means Octopushy's own, see <see cref="ManagedHostKey"/>.</summary>
    StoreReference? HostKey,
    JsonObject Vars,
    IReadOnlyDictionary<string, StoreReference> SecretVars,
    IReadOnlyDictionary<string, StoreReference> SecretFiles,
    string Hash)
{
    /// <summary>Fields of <c>access</c> that only configure runs use. None of them reaches OpenTofu.</summary>
    public static readonly string[] AccessOnly = ["address", "port", "user", "sshKey", "hostKey"];

    /// <summary>
    /// The store keys <c>secrets</c> and <c>secretFiles</c> name without a version, so a run takes
    /// whichever version is active. A new active version of one of them starts the setup action.
    /// A key pinned to a version in Git changes only with a commit, and a <c>system/</c> key, which
    /// validation refuses here anyway, rotates on its own terms.
    /// </summary>
    public IReadOnlyList<string> FollowedSecrets => FollowedSecretsOf(Guest);

    /// <inheritdoc cref="FollowedSecrets"/>
    public static IReadOnlyList<string> FollowedSecretsOf(GuestDefinition guest) =>
        Secrets(guest, "secrets").Values.Concat(Secrets(guest, "secretFiles").Values)
            .Where(r => r.Version is null && !r.Key.StartsWith(LabRepositoryLoader.SystemScope, StringComparison.Ordinal))
            .Select(r => r.Key).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>Where a configure run connects: the declared address, or the guest's own.</summary>
    public static string? AddressOf(GuestDefinition guest) =>
        guest.Json["access"]?["address"]?.GetValue<string>() ?? ProvisionInputs.ExpectedIpv4(guest);

    /// <summary>A store reference in <c>access</c>.</summary>
    public static StoreReference? Reference(GuestDefinition guest, string field) =>
        guest.Json["access"]?[field] is JsonObject reference ? Parse(reference) : null;

    /// <summary>A guest's <c>secrets</c> or <c>secretFiles</c>.</summary>
    public static IReadOnlyDictionary<string, StoreReference> Secrets(GuestDefinition guest, string field) =>
        (guest.Json[field] as JsonObject)?.ToDictionary(p => p.Key, p => Parse(p.Value!), StringComparer.Ordinal) ?? [];

    private static StoreReference Parse(JsonNode node) =>
        new(node["store"]!.GetValue<string>(), node["version"]?.GetValue<int>());

    /// <summary>Builds the inputs for a valid guest whose workflow has a setup stage; the repository must have validated it.</summary>
    public static ConfigureInputs Build(LabRepository repository, GuestDefinition guest)
    {
        var workflow = guest.Workflow!;
        var stage = workflow.Setup ?? throw new InvalidOperationException($"{workflow.Label} has no setup stage.");
        var inputs = guest.Json["inputs"]?.DeepClone().AsObject() ?? [];
        // A schedule says when an action runs, not what it does, so changing one configures nothing.
        var hashed = new JsonObject
        {
            ["inputs"] = inputs.DeepClone(),
            ["secrets"] = guest.Json["secrets"]?.DeepClone(),
            ["secretFiles"] = guest.Json["secretFiles"]?.DeepClone(),
            ["access"] = guest.Json["access"]?.DeepClone(),
            ["address"] = AddressOf(guest),
            ["workflow"] = ProvisionInputs.WorkflowHash(workflow, [stage.Root]),
            // A service has its own runs: a change to one does not configure its guest again.
            ["guestFiles"] = ProvisionInputs.TreeHash(repository.Root, guest.Directory, "guest.yaml", "services"),
        };
        return new ConfigureInputs(
            guest,
            workflow,
            stage,
            stage.Configure!,
            AddressOf(guest)!,
            guest.Json["access"]?["port"]?.GetValue<int>() ?? 22,
            guest.Json["access"]!["user"]!.GetValue<string>(),
            Reference(guest, "sshKey"),
            Reference(guest, "hostKey"),
            inputs,
            Secrets(guest, "secrets"),
            Secrets(guest, "secretFiles"),
            Canonical.Hash(hashed));
    }
}
