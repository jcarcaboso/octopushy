namespace Octopushy.Definitions;

/// <summary>A reference resolved: the workflow it names, or why there is none.</summary>
public sealed record WorkflowResolution(WorkflowDefinition? Workflow, string? Problem)
{
    public static WorkflowResolution Found(WorkflowDefinition workflow) => new(workflow, null);

    public static WorkflowResolution NotFound(string problem) => new(null, problem);
}

/// <summary>
/// Where guests and services find the workflows they name. <see cref="LocalWorkflowSource"/> reads a directory's own
/// <c>workflows/</c>; <see cref="GitTagWorkflowSource"/> reads each workflow from the commit its release tag recorded in a
/// separate workflows repository.
/// </summary>
public interface IWorkflowSource
{
    /// <summary>How messages name the source, such as "workflows/ in this repository" or a repository's URL.</summary>
    string Name { get; }

    /// <summary>Every workflow the source read, valid or not.</summary>
    IReadOnlyList<WorkflowDefinition> Workflows { get; }

    /// <summary>What was wrong with them. Each issue's address is its workflow's, <c>workflows/&lt;id&gt;</c>.</summary>
    IReadOnlyList<DefinitionIssue> Issues { get; }

    /// <summary>The newest version <paramref name="reference"/> matches, or why none does.</summary>
    WorkflowResolution Resolve(WorkflowReference reference);
}

/// <summary>
/// The workflows in a directory's own <c>workflows/</c> folder: a lab repository's, or a workflows repository's working
/// tree read as it is. Each has one version, the one its <c>workflow.yaml</c> declares: <c>@M</c> matches it when the major
/// is <c>M</c>, and <c>@M.m</c> only when it is exactly <c>M.m</c>.
/// </summary>
public sealed class LocalWorkflowSource : IWorkflowSource
{
    private readonly WorkflowReader _reader;

    /// <param name="root">The directory that holds <c>workflows/</c> and <c>modules/</c>.</param>
    /// <param name="name">How messages name it; by default, the lab repository's own folder.</param>
    public LocalWorkflowSource(string root, string? name = null)
    {
        Root = Path.GetFullPath(root);
        Name = name ?? "workflows/ in this repository";
        _reader = new WorkflowReader(Root);
        Workflows = _reader.ReadAll();
    }

    /// <summary>The directory read, as a full path.</summary>
    public string Root { get; }

    public string Name { get; }

    public IReadOnlyList<WorkflowDefinition> Workflows { get; }

    public IReadOnlyList<DefinitionIssue> Issues => _reader.Issues;

    public WorkflowResolution Resolve(WorkflowReference reference)
    {
        var candidates = Workflows.Where(w => w.Id == reference.Id).ToList();
        if (candidates.Count == 0)
        {
            return WorkflowResolution.NotFound(_reader.Unreadable.Contains(reference.Id)
                ? $"{reference} not found: workflows/{reference.Id}/workflow.yaml could not be read, see its errors."
                : $"{reference} not found: there is no workflows/{reference.Id}/workflow.yaml.");
        }

        if (candidates.Where(w => reference.Matches(w.Version)).MaxBy(w => w.Version) is { } match)
            return WorkflowResolution.Found(match);
        var available = string.Join(", ", candidates.Select(w => w.Version).Order().Select(v => v.ToString()));
        return WorkflowResolution.NotFound($"{reference} not found; available {available}.");
    }
}

/// <summary>A source for a repository that names a workflows repository when nothing resolved its tags, as for a plain load.</summary>
public sealed class UnresolvedWorkflowSource(string repository) : IWorkflowSource
{
    public string Name => repository;

    public IReadOnlyList<WorkflowDefinition> Workflows => [];

    public IReadOnlyList<DefinitionIssue> Issues => [];

    public WorkflowResolution Resolve(WorkflowReference reference) =>
        WorkflowResolution.NotFound($"{reference} is not resolved: workflows come from {repository}, and this load was not given its releases.");
}

/// <summary>
/// Workflows from a separate workflows repository, released by tags. Each reference has already been resolved to a release,
/// <c>(id, version, commit)</c>, by <see cref="WorkflowResolver"/>, and this reads the workflow from that commit's files in
/// <paramref name="trees"/>: <c>workflows/&lt;id&gt;/</c> and <c>modules/</c> there, checked as a local workflow is. The same
/// resolutions always read the same workflows, so a revision is read again exactly as it was first read.
/// </summary>
/// <param name="repository">The workflows repository, as messages name it.</param>
/// <param name="resolutions">What each reference resolved to.</param>
/// <param name="trees">The directory holding the files of each commit; a commit missing here cannot be read.</param>
public sealed class GitTagWorkflowSource(string repository, IEnumerable<ResolvedWorkflow> resolutions, IReadOnlyDictionary<string, string> trees)
    : IWorkflowSource
{
    private readonly Dictionary<string, ResolvedWorkflow> _resolutions = resolutions.ToDictionary(r => r.Reference, StringComparer.Ordinal);
    private readonly Dictionary<(string Commit, string Id), WorkflowDefinition?> _read = [];
    private readonly List<DefinitionIssue> _issues = [];

    public string Repository { get; } = repository;

    public string Name => Repository;

    /// <summary>What each reference resolved to, ordered by the reference.</summary>
    public IReadOnlyList<ResolvedWorkflow> Resolutions => _resolutions.Values.OrderBy(r => r.Reference, StringComparer.Ordinal).ToList();

    public IReadOnlyList<WorkflowDefinition> Workflows => _read.Values.OfType<WorkflowDefinition>()
        .OrderBy(w => w.Id, StringComparer.Ordinal).ThenBy(w => w.Version).ToList();

    public IReadOnlyList<DefinitionIssue> Issues => _issues;

    public WorkflowResolution Resolve(WorkflowReference reference)
    {
        if (!_resolutions.TryGetValue(reference.ToString(), out var resolved))
            return WorkflowResolution.NotFound($"{reference} was not resolved against {Repository}.");
        if (resolved.Problem is { } problem)
            return WorkflowResolution.NotFound(problem);
        var label = resolved.Label!;
        var commit = resolved.Commit!;
        if (!_read.TryGetValue((commit, reference.Id), out var workflow))
        {
            workflow = Read(reference.Id, commit, label);
            _read[(commit, reference.Id)] = workflow;
        }

        if (workflow is null)
            return WorkflowResolution.NotFound(
                $"{reference} resolved to {label} at {WorkflowResolver.Short(commit)}, and its workflows/{reference.Id}/workflow.yaml could not be read, see its errors.");
        return workflow.Version.ToString() == resolved.Version
            ? WorkflowResolution.Found(workflow)
            : WorkflowResolution.NotFound($"{reference} resolved to {label}, and the workflow.yaml at {WorkflowResolver.Short(commit)} declares {workflow.Version}.");
    }

    private WorkflowDefinition? Read(string id, string commit, string label)
    {
        if (!trees.TryGetValue(commit, out var tree))
        {
            _issues.Add(new DefinitionIssue(IssueSeverity.Error, $"workflows/{id}/workflow.yaml", null,
                $"{label}: the commit {WorkflowResolver.Short(commit)} of {Repository} could not be read.", $"workflows/{id}"));
            return null;
        }

        var reader = new WorkflowReader(tree);
        var workflow = reader.ReadOne(id);
        // Its issues name files of the workflows repository, so the release they are in leads.
        _issues.AddRange(reader.Issues.Select(i => i with { Message = $"{label} at {WorkflowResolver.Short(commit)}: {i.Message}" }));
        return workflow is null ? null : workflow with { Commit = commit, Repository = Repository };
    }
}
