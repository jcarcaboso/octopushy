namespace Octopushy.Definitions;

/// <summary>The definitions read from one snapshot of a lab repository.</summary>
public sealed class LabRepository(
    string root,
    RepositoryDefinition? settings,
    IReadOnlyList<LabDefinition> labs,
    IReadOnlyList<InstallationDefinition> installations,
    IReadOnlyList<GuestDefinition> guests,
    IReadOnlyList<ClusterDefinition> clusters,
    IReadOnlyDictionary<string, TemplateDefinition> templates,
    IReadOnlyDictionary<string, RecipeDefinition> recipes,
    IReadOnlyList<ResourceEntry> resources,
    IReadOnlyList<DefinitionIssue> issues,
    IReadOnlyList<StoreUse> storeUses,
    bool repositoryValid)
{
    /// <summary>The directory the snapshot was read from.</summary>
    public string Root { get; } = root;

    /// <summary><c>octopushy.yaml</c>, or null when it is missing or invalid.</summary>
    public RepositoryDefinition? Settings { get; } = settings;

    public IReadOnlyList<LabDefinition> Labs { get; } = labs;

    public IReadOnlyList<InstallationDefinition> Installations { get; } = installations;

    public IReadOnlyList<GuestDefinition> Guests { get; } = guests;

    public IReadOnlyList<ClusterDefinition> Clusters { get; } = clusters;

    public IReadOnlyDictionary<string, TemplateDefinition> Templates { get; } = templates;

    public IReadOnlyDictionary<string, RecipeDefinition> Recipes { get; } = recipes;

    /// <summary>Every lab, installation, guest, and cluster found, including those whose definition is invalid.</summary>
    public IReadOnlyList<ResourceEntry> Resources { get; } = resources;

    public IReadOnlyList<DefinitionIssue> Issues { get; } = issues;

    public IReadOnlyList<StoreUse> StoreUses { get; } = storeUses;

    /// <summary>False when <c>octopushy.yaml</c> is missing or invalid; then nothing is valid.</summary>
    public bool RepositoryValid { get; } = repositoryValid;

    public LabDefinition? Lab(string id) => Labs.FirstOrDefault(l => l.Id == id);

    public InstallationDefinition? Installation(string address) => Installations.FirstOrDefault(i => i.Address == address);

    public GuestDefinition? Guest(string address) => Guests.FirstOrDefault(g => g.Address == address);

    public ClusterDefinition? Cluster(string address) => Clusters.FirstOrDefault(c => c.Address == address);

    /// <summary>The cluster that lists a guest, if any.</summary>
    public ClusterDefinition? ClusterOf(string guestAddress) =>
        Clusters.FirstOrDefault(c => c.Members.Any(m => m.Guest == guestAddress));

    public ResourceEntry? Resource(string address) => Resources.FirstOrDefault(r => r.Address == address);

    public bool IsValid(string address) => Resource(address)?.Valid ?? false;

    /// <summary>Issues of a resource itself, of the shared definitions it uses, and of the repository.</summary>
    public IEnumerable<DefinitionIssue> IssuesFor(string address)
    {
        var guest = Guest(address);
        var cluster = Cluster(address);
        return Issues.Where(i =>
            i.Address == address
            || i.Address is null
            || (cluster is not null && i.Address == $"recipes/{cluster.Recipe}")
            || (guest is not null && (i.Address == $"recipes/{guest.Recipe}" || (guest.Template is { } t && i.Address == $"templates/{t}"))));
    }
}
