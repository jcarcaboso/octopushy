using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Inventory;

/// <summary>Pipeline runs of a service, under the same rules as a guest's.</summary>
public sealed partial class Planner
{
    /// <summary>Deploys a service at the latest revision, whatever its last run did, once its guest is ready.</summary>
    public Task<Guid> DeployServiceAsync(Guid resourceId, CancellationToken cancellationToken) =>
        engine.ExecuteAsync((db, _) => DeployServiceAsync(db, resourceId, cancellationToken), cancellationToken);

    private async Task<Guid> DeployServiceAsync(OctopushyDb db, Guid resourceId, CancellationToken cancellationToken)
    {
        var (resource, revision, repository) = await LoadAsync(db, resourceId, ResourceKinds.Service, cancellationToken);
        var service = repository.Service(resource.Address) ?? throw new RunException("The service is not in the latest revision.");
        var guest = await db.Resources.FirstOrDefaultAsync(r => r.Address == service.GuestAddress && r.Kind == ResourceKinds.Guest && r.RetiredAt == null, cancellationToken)
            ?? throw new RunException("The service's guest is not known yet.");
        if (await GuestWork.WaitingForAsync(db, guest, repository, cancellationToken, service) is { } waiting)
            throw new RunException(waiting);
        var state = await db.GuestConfigurations.FirstOrDefaultAsync(c => c.ResourceId == resource.Id, cancellationToken);
        var images = await ComposeImagesAsync(db, service, revision, state, cancellationToken);
        var run = factory.Pipeline(repository, service, resource, guest, revision, RunReasons.Manual, images);
        if (state is not null)
            state.ProposedHash = run.InputsHash;
        await engine.AddRunsAsync(db, [run], cancellationToken);
        return run.Id;
    }

    /// <summary>
    /// Proposes a run of each service whose inputs changed and whose guest is ready. Each input hash gets one
    /// automatic run, so a failure waits for a new commit or a person instead of repeating.
    /// </summary>
    public Task SchedulePipelinesAsync(CancellationToken cancellationToken) =>
        engine.ExecuteAsync(async (db, _) =>
        {
            // The head, not the newest loaded, as for a guest's configuration. An invalid head proposes nothing.
            var revision = await RepositoryTrust.HeadRevisions(db).AsNoTracking().Where(r => r.RepositoryValid)
                .OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
            if (revision is null) return 0;

            var due = await db.GuestConfigurations
                .Where(c => c.DesiredHash != null && c.DesiredHash != c.ProposedHash)
                .Join(db.Resources.Where(r => r.Kind == ResourceKinds.Service && r.Valid && r.RetiredAt == null && r.MissingSince == null
                        && !db.Runs.Any(run => run.ResourceId == r.Id && run.Status != RunStatuses.Succeeded
                            && run.Status != RunStatuses.Failed && run.Status != RunStatuses.Cancelled)),
                    c => c.ResourceId, r => r.Id, (c, r) => new { State = c, Resource = r })
                .ToListAsync(cancellationToken);
            if (due.Count == 0) return 0;

            var repository = await revisions.GetAsync(revision, cancellationToken);
            var runs = new List<Run>();
            foreach (var item in due.OrderBy(i => i.Resource.Address, StringComparer.Ordinal))
            {
                if (repository.Resource(item.Resource.Address)?.Valid != true || repository.Service(item.Resource.Address) is not { } service)
                    continue;
                var guest = await db.Resources.FirstOrDefaultAsync(r => r.Address == service.GuestAddress && r.Kind == ResourceKinds.Guest && r.RetiredAt == null, cancellationToken);
                if (guest is null || await GuestWork.WaitingForAsync(db, guest, repository, cancellationToken, service) is not null)
                    continue;
                var images = await ComposeImagesAsync(db, service, revision, item.State, cancellationToken);
                var run = factory.Pipeline(repository, service, item.Resource, guest, revision, RunReasons.Revision, images);
                item.State.DesiredHash = run.InputsHash;
                item.State.ProposedHash = run.InputsHash;
                runs.Add(run);
            }

            await engine.AddRunsAsync(db, runs, cancellationToken);
            return runs.Count;
        }, cancellationToken);

    /// <summary>
    /// The images the service's compose file names at <paramref name="revision"/>'s commit and at the commit last applied
    /// for the service, the one its page calls applied. Local reads of the mirror, as <see cref="ChangedSinceAsync"/> makes
    /// for a configure run, and no network. Null when the folder has no compose file at the run's commit, and on any problem
    /// reading either side: what a run shows never stops it being made.
    /// </summary>
    private async Task<ComposeImagesContext?> ComposeImagesAsync(OctopushyDb db, ServiceDefinition service, DefinitionRevision revision,
        GuestConfiguration? state, CancellationToken cancellationToken)
    {
        try
        {
            if (await ComposeImagesAtAsync(revision.Commit, service.Directory, cancellationToken) is not { } after)
                return null;
            var baseline = state?.AppliedRevisionId is { } id
                ? await db.Revisions.AsNoTracking().Where(r => r.Id == id).Select(r => r.Commit).FirstOrDefaultAsync(cancellationToken)
                : null;
            IReadOnlyDictionary<string, string> before = new Dictionary<string, string>();
            if (baseline == revision.Commit)
                before = after;
            else if (baseline is not null)
            {
                // A baseline commit or file that cannot be read is not a first deploy: show nothing rather than every image as new.
                if (ComposeFile(await mirror.TreeAsync(baseline, cancellationToken), service.Directory) is { } file)
                    before = await ComposeImagesOfAsync(baseline, file, cancellationToken) ?? throw new GitException("The applied compose file cannot be read.");
            }

            var images = after.Keys.Union(before.Keys).Order(StringComparer.Ordinal)
                .Select(name => new ComposeImageChange(name, before.GetValueOrDefault(name), after.GetValueOrDefault(name)))
                .ToList();
            return new ComposeImagesContext(baseline, images);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The images of the compose file in <paramref name="directory"/> at <paramref name="commit"/>; null when it has none or it cannot be read.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> ComposeImagesAtAsync(string commit, string directory, CancellationToken cancellationToken) =>
        ComposeFile(await mirror.TreeAsync(commit, cancellationToken), directory) is { } file
            ? await ComposeImagesOfAsync(commit, file, cancellationToken)
            : null;

    private async Task<IReadOnlyDictionary<string, string>?> ComposeImagesOfAsync(string commit, TreeFile file, CancellationToken cancellationToken) =>
        file.IsRegular && file.Size <= ComposeImages.MaxFileBytes && (await mirror.ReadFileAsync(commit, file.Path, cancellationToken))?.Text is { } text
            ? ComposeImages.Read(text)
            : null;

    /// <summary>The service's compose file in the tree, whatever its mode.</summary>
    private static TreeFile? ComposeFile(IReadOnlyList<TreeFile> tree, string directory) =>
        ComposeImages.FileIn(directory, tree.Select(f => f.Path)) is { } path ? tree.First(f => f.Path == path) : null;
}
