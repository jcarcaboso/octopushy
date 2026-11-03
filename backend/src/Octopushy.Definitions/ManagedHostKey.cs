using System.Text.Json.Nodes;

namespace Octopushy.Definitions;

/// <summary>Where a host can receive a guest's cloud-init snippet: its sshd, and the key it is pinned to.</summary>
public sealed record SnippetHost(string Address, int Port, StoreReference HostKey);

/// <summary>An installation's snippet uploads, from its <c>snippets</c> block.</summary>
public sealed record SnippetSettings(string Datastore, string User, IReadOnlyDictionary<string, SnippetHost> Hosts)
{
    public const string DefaultDatastore = "local";
    public const string DefaultUser = "octopushy-snippets";
}

/// <summary>
/// Which guests have an SSH host key of Octopushy's. Octopushy generates it, a provision run
/// delivers it in a cloud-init snippet, and configure runs pin it, so nobody reads a key off a
/// console. That needs three things: a guest Octopushy logs in to, a provision recipe whose
/// module installs the key, and a host set up for snippet uploads. A guest that names
/// <c>access.hostKey</c> keeps its own: that is how a machine Octopushy did not create is reached.
/// </summary>
public static class ManagedHostKey
{
    /// <summary>The installation's snippet settings, or null when it has none.</summary>
    public static SnippetSettings? Settings(InstallationDefinition installation)
    {
        if (installation.Json["snippets"] is not JsonObject snippets)
            return null;
        var hosts = new Dictionary<string, SnippetHost>(StringComparer.Ordinal);
        foreach (var (id, node) in (snippets["hosts"] as JsonObject) ?? [])
        {
            if (node is not JsonObject host || host["hostKey"]?["store"]?.GetValue<string>() is not { } key)
                continue;
            var address = host["address"]?.GetValue<string>()
                ?? (installation.Hosts.TryGetValue(id, out var definition)
                    && Uri.TryCreate(ProvisionInputs.EndpointFor(installation, definition), UriKind.Absolute, out var endpoint) ? endpoint.Host : null);
            if (address is null)
                continue;
            hosts[id] = new SnippetHost(address, host["port"]?.GetValue<int>() ?? 22, new StoreReference(key, host["hostKey"]?["version"]?.GetValue<int>()));
        }

        return new SnippetSettings(
            snippets["datastore"]?.GetValue<string>() ?? SnippetSettings.DefaultDatastore,
            snippets["user"]?.GetValue<string>() ?? SnippetSettings.DefaultUser,
            hosts);
    }

    /// <summary>Whether the recipe's module installs the key Octopushy gives it.</summary>
    public static bool Delivers(RecipeDefinition recipe) =>
        recipe.Operation == "provision" && recipe.Json["hostKey"]?.GetValue<string>() == "managed";

    /// <summary>Whether Octopushy could give this guest a key: its recipe delivers one, to a host set up for it.</summary>
    public static bool Offered(LabRepository repository, GuestDefinition guest) =>
        Offered(repository.Recipes, repository.Installation(guest.InstallationAddress), guest);

    /// <inheritdoc cref="Offered(LabRepository, GuestDefinition)"/>
    public static bool Offered(IReadOnlyDictionary<string, RecipeDefinition> recipes, InstallationDefinition? installation, GuestDefinition guest) =>
        recipes.TryGetValue(guest.Recipe, out var recipe) && Delivers(recipe)
        && installation is not null && Settings(installation)?.Hosts.ContainsKey(guest.Host) == true;

    /// <summary>
    /// A guest whose host key is Octopushy's: one Octopushy provisions and runs Ansible work on, from
    /// a setup recipe or a service stage, as for its access key in <see cref="ManagedAccess.Wanted"/>,
    /// where it is offered, unless it names <c>access.hostKey</c>, which always wins.
    /// </summary>
    /// <param name="keyIssued">
    /// Whether Octopushy already holds a host key for this guest. Once it does, the guest keeps it while it
    /// is offered, whatever happens to its Ansible work: dropping the snippet would replace the VM.
    /// </param>
    public static bool Wanted(LabRepository repository, GuestDefinition guest, bool keyIssued) =>
        guest.Json["access"]?["hostKey"] is null
        && guest.ManagedWorker is null
        && !guest.Retired
        && (keyIssued || guest.SetupRecipe is not null || ManagedAccess.RunsPlaybooks(repository, guest))
        && Offered(repository, guest);
}
