namespace Octopushy.Definitions;

/// <summary>The definitions read from one snapshot of a lab repository, with the workflows they resolved to.</summary>
public sealed class LabRepository(
    string root,
    RepositoryDefinition? settings,
    IReadOnlyList<LabDefinition> labs,
    IReadOnlyList<InstallationDefinition> installations,
    IReadOnlyList<GuestDefinition> guests,
    IReadOnlyList<ServiceDefinition> services,
    IReadOnlyDictionary<string, TemplateDefinition> templates,
    IReadOnlyList<WorkflowDefinition> workflows,
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

    public IReadOnlyList<ServiceDefinition> Services { get; } = services;

    public IReadOnlyDictionary<string, TemplateDefinition> Templates { get; } = templates;

    /// <summary>Every workflow the workflow source read, valid or not, whether anything names it or not.</summary>
    public IReadOnlyList<WorkflowDefinition> Workflows { get; } = workflows;

    /// <summary>Every lab, installation, guest, and service found, including those whose definition is invalid.</summary>
    public IReadOnlyList<ResourceEntry> Resources { get; } = resources;

    public IReadOnlyList<DefinitionIssue> Issues { get; } = issues;

    public IReadOnlyList<StoreUse> StoreUses { get; } = storeUses;

    /// <summary>False when <c>octopushy.yaml</c> is missing or invalid; then nothing is valid.</summary>
    public bool RepositoryValid { get; } = repositoryValid;

    public LabDefinition? Lab(string id) => Labs.FirstOrDefault(l => l.Id == id);

    public InstallationDefinition? Installation(string address) => Installations.FirstOrDefault(i => i.Address == address);

    public GuestDefinition? Guest(string address) => Guests.FirstOrDefault(g => g.Address == address);

    public ServiceDefinition? Service(string address) => Services.FirstOrDefault(s => s.Address == address);

    public ResourceEntry? Resource(string address) => Resources.FirstOrDefault(r => r.Address == address);

    public bool IsValid(string address) => Resource(address)?.Valid ?? false;

    /// <summary>Issues of a resource itself, of the shared definitions it uses, such as its workflow, and of the repository.</summary>
    public IEnumerable<DefinitionIssue> IssuesFor(string address)
    {
        var guest = Guest(address);
        // The workflow it names, whether or not that resolved: a workflow that cannot be read is why it did not.
        var reference = guest?.WorkflowReference ?? Service(address)?.WorkflowReference;
        var workflow = reference is null ? null : $"workflows/{reference.Id}";
        return Issues.Where(i =>
            i.Address == address
            || i.Address is null
            || (workflow is not null && i.Address == workflow)
            || (guest?.Template is { } t && i.Address == $"templates/{t}"));
    }
}
