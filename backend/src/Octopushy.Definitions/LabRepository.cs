namespace Octopushy.Definitions;

/// <summary>The definitions read from one snapshot of a lab repository.</summary>
public sealed class LabRepository(
    string root,
    RepositoryDefinition? settings,
    IReadOnlyList<LabDefinition> labs,
    IReadOnlyList<InstallationDefinition> installations,
    IReadOnlyList<GuestDefinition> guests,
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

    public IReadOnlyDictionary<string, TemplateDefinition> Templates { get; } = templates;

    public IReadOnlyDictionary<string, RecipeDefinition> Recipes { get; } = recipes;

    /// <summary>Every lab, installation, and guest found, including those whose definition is invalid.</summary>
    public IReadOnlyList<ResourceEntry> Resources { get; } = resources;

    public IReadOnlyList<DefinitionIssue> Issues { get; } = issues;

    public IReadOnlyList<StoreUse> StoreUses { get; } = storeUses;

    /// <summary>False when <c>octopushy.yaml</c> is missing or invalid; then nothing is valid.</summary>
    public bool RepositoryValid { get; } = repositoryValid;

    public LabDefinition? Lab(string id) => Labs.FirstOrDefault(l => l.Id == id);

    public InstallationDefinition? Installation(string address) => Installations.FirstOrDefault(i => i.Address == address);

    public GuestDefinition? Guest(string address) => Guests.FirstOrDefault(g => g.Address == address);

    public ResourceEntry? Resource(string address) => Resources.FirstOrDefault(r => r.Address == address);

    public bool IsValid(string address) => Resource(address)?.Valid ?? false;

    /// <summary>Issues of a resource itself, of the shared definitions it uses, and of the repository.</summary>
    public IEnumerable<DefinitionIssue> IssuesFor(string address)
    {
        var guest = Guest(address);
        return Issues.Where(i =>
            i.Address == address
            || i.Address is null
            || (guest is not null && (i.Address == $"recipes/{guest.Recipe}" || (guest.Template is { } t && i.Address == $"templates/{t}"))));
    }
}
