using Octopushy.Server.Data;
using Octopushy.Server.Git;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// The files an attempt's agent receives. They are the lab repository at the run's commit and, for a workflow from a
/// workflows repository, that repository's <c>workflows/</c> and <c>modules/</c> at the commit the workflow resolved to,
/// merged at the root. Paths stay what they are in each repository, so a stage's root and a guest's directory read the same
/// as with workflows kept in the lab repository.
/// </summary>
public sealed class RunBundles(RepositoryMirror mirror, WorkflowsMirrors workflows)
{
    public Task<byte[]> ForAsync(Run run, RunContext context, CancellationToken cancellationToken) =>
        context.Workflow is { Repository: { } repository } workflow
            ? ForAsync(run.Commit!, repository, workflow.Commit, workflow.Label, cancellationToken)
            : mirror.BundleAsync(run.Commit!, cancellationToken);

    /// <summary>
    /// The lab repository at <paramref name="commit"/> with, when <paramref name="repository"/> is set, the workflows repository's
    /// <c>workflows/</c> and <c>modules/</c> at <paramref name="workflowCommit"/>: as a run gets it, for work that is not a run.
    /// </summary>
    public async Task<byte[]> ForAsync(string commit, string? repository, string? workflowCommit, string label, CancellationToken cancellationToken)
    {
        if (repository is null || workflowCommit is null)
            return await mirror.BundleAsync(commit, cancellationToken);
        string snapshot;
        try
        {
            snapshot = await workflows.For(repository).SnapshotAsync(workflowCommit, cancellationToken);
        }
        catch (GitException e)
        {
            throw new AssignmentException($"{label} at {workflowCommit[..Math.Min(10, workflowCommit.Length)]} cannot be read from {repository}: {e.Message}");
        }

        return await mirror.BundleAsync(commit, cancellationToken, snapshot, workflowCommit);
    }
}
