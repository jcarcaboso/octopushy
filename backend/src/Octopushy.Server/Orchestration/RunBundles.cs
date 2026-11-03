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
    public async Task<byte[]> ForAsync(Run run, RunContext context, CancellationToken cancellationToken)
    {
        if (context.Workflow is not { Repository: { } repository } workflow)
            return await mirror.BundleAsync(run.Commit!, cancellationToken);
        string snapshot;
        try
        {
            snapshot = await workflows.For(repository).SnapshotAsync(workflow.Commit, cancellationToken);
        }
        catch (GitException e)
        {
            throw new AssignmentException($"{workflow.Label} at {workflow.Commit[..Math.Min(10, workflow.Commit.Length)]} cannot be read from {repository}: {e.Message}");
        }

        return await mirror.BundleAsync(run.Commit!, cancellationToken, snapshot, workflow.Commit);
    }
}
