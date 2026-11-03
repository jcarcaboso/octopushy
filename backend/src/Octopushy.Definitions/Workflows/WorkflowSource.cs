namespace Octopushy.Definitions;

/// <summary>A reference resolved: the workflow it names, or why there is none.</summary>
public sealed record WorkflowResolution(WorkflowDefinition? Workflow, string? Problem)
{
    public static WorkflowResolution Found(WorkflowDefinition workflow) => new(workflow, null);

    public static WorkflowResolution NotFound(string problem) => new(null, problem);
}

/// <summary>
/// Where guests and services find the workflows they name. <see cref="LocalWorkflowSource"/> reads the lab repository's own
/// <c>workflows/</c>; a source that resolves versions from tags in a workflows repository is planned to sit behind the
/// same interface.
/// </summary>
public interface IWorkflowSource
{
    /// <summary>How messages name the source, such as "workflows/ in this repository".</summary>
    string Name { get; }

    /// <summary>Every workflow the source read, valid or not.</summary>
    IReadOnlyList<WorkflowDefinition> Workflows { get; }

    /// <summary>What was wrong with them. Each issue's address is its workflow's, <c>workflows/&lt;id&gt;</c>.</summary>
    IReadOnlyList<DefinitionIssue> Issues { get; }

    /// <summary>The newest version <paramref name="reference"/> matches, or why none does.</summary>
    WorkflowResolution Resolve(WorkflowReference reference);
}

/// <summary>
/// The workflows in a lab repository's own <c>workflows/</c> folder. Each has one version, the one its <c>workflow.yaml</c>
/// declares: <c>@M</c> matches it when the major is <c>M</c>, and <c>@M.m</c> only when it is exactly <c>M.m</c>.
/// </summary>
public sealed class LocalWorkflowSource : IWorkflowSource
{
    private readonly WorkflowReader _reader;

    public LocalWorkflowSource(string repositoryRoot)
    {
        _reader = new WorkflowReader(repositoryRoot);
        Workflows = _reader.ReadAll();
    }

    public string Name => "workflows/ in this repository";

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
