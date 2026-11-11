using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Definitions;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Images;
using Octopushy.Server.Inventory;

namespace Octopushy.Server.Orchestration;

/// <summary>Why an agent may not send or fetch an image.</summary>
public sealed class ImageAccessException(string message) : Exception(message);

/// <summary>
/// Built templates' images: their builds, a kind of attempt that belongs to no run and no installation, and what the server
/// keeps of them. A build carries no secrets and no Proxmox context, and connects to nothing in the lab: it builds code from
/// the workflows repository at a resolved release, with at most a lock file from the lab repository, which is the same code
/// a guest's setup already runs, with less authority. So it needs no installation's trust, no labels and no evidence of
/// reachability: any connected agent that advertises <c>nix</c> and has a free slot may take it. See docs/built-templates.md.
/// </summary>
public sealed partial class RunEngine
{
    /// <summary>
    /// Queues a build for each built template the newest valid revision needs whose key has no image and no build under way.
    /// A failed key waits for a new key or a person: see <see cref="RetryImageBuildAsync"/>. Then applies retention.
    /// </summary>
    public Task<int> QueueImageBuildsAsync(CancellationToken cancellationToken) =>
        WithDbAsync(async (db, _) =>
        {
            if (await NewestValidAsync(db, cancellationToken) is not { } newest)
                return 0;
            var (revision, repository) = newest;
            var queued = 0;
            // Two templates may build the same key: it is one image.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var template in TemplateImages.Wanted(repository))
            {
                var build = template.Build!;
                if (!seen.Add(build.Key))
                    continue;
                var row = await db.ImageBuilds.FirstOrDefaultAsync(b => b.Key == build.Key, cancellationToken);
                if (row is null)
                {
                    row = NewImageBuild(template, build, revision);
                    db.ImageBuilds.Add(row);
                    QueueImageAttempt(db, row);
                    queued++;
                }
                else if (row.Status == ImageBuildStatuses.Deleted)
                {
                    Requeue(row, template, revision);
                    QueueImageAttempt(db, row);
                    queued++;
                }
                else if (row.Status == ImageBuildStatuses.Queued && row.RevisionId != revision.Id && row.AttemptId is { } waiting
                         && await db.Attempts.FirstOrDefaultAsync(a => a.Id == waiting && a.Status == AttemptStatuses.Queued, cancellationToken) is { } attempt)
                {
                    // Still waiting for an agent: it takes its files from the newest revision, whose lock is the same by its key.
                    (row.RevisionId, row.RevisionCommit, attempt.RevisionId) = (revision.Id, revision.Commit, revision.Id);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await RetainImagesAsync(db, repository, cancellationToken);
            if (queued > 0)
            {
                logger.LogInformation("Queued {Count} image build(s) for built templates.", queued);
                signal.Wake();
            }

            return queued;
        }, cancellationToken);

    /// <summary>
    /// Queues the build of a template's current key again, after it failed, or after its image was deleted. A build under way
    /// or done is refused.
    /// </summary>
    public Task<ImageBuild> RetryImageBuildAsync(string templateId, CancellationToken cancellationToken) =>
        WithDbAsync(async (db, _) =>
        {
            var (revision, repository) = await NewestValidAsync(db, cancellationToken)
                ?? throw new RunException("Sync a valid revision of the current repository source first.");
            var template = repository.Templates.GetValueOrDefault(templateId)
                ?? throw new RunException($"There is no template {templateId} in the newest valid revision.");
            if (template.Build is not { } build || !template.Valid)
                throw new RunException(template.Build is null
                    ? $"The template {templateId} downloads its image; there is nothing to build."
                    : $"The template {templateId} has errors: see its issues.");
            var row = await db.ImageBuilds.FirstOrDefaultAsync(b => b.Key == build.Key, cancellationToken);
            if (row is not null && ImageBuildStatuses.InProgress(row.Status))
                throw new RunException($"A build of the image of {templateId} is already {row.Status}.");
            if (row?.Status == ImageBuildStatuses.Built)
                throw new RunException($"The image of {templateId} is built already.");
            if (row is null)
            {
                row = NewImageBuild(template, build, revision);
                db.ImageBuilds.Add(row);
            }
            else
                Requeue(row, template, revision);

            QueueImageAttempt(db, row);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("The build of the image of {Template} ({Key}) was queued again.", templateId, build.Key);
            signal.Wake();
            return row;
        }, cancellationToken);

    private ImageBuild NewImageBuild(TemplateDefinition template, BuildSource build, DefinitionRevision revision) => new()
    {
        Id = Guid.CreateVersion7(),
        Key = build.Key,
        TemplateId = template.Id,
        WorkflowId = build.WorkflowId,
        WorkflowVersion = build.WorkflowVersion,
        WorkflowCommit = build.WorkflowCommit,
        WorkflowRepository = build.WorkflowRepository,
        Output = build.Output,
        LockPath = build.LockPath,
        RevisionId = revision.Id,
        RevisionCommit = revision.Commit,
        CreatedAt = clock.GetUtcNow(),
    };

    private static void Requeue(ImageBuild row, TemplateDefinition template, DefinitionRevision revision)
    {
        row.TemplateId = template.Id;
        row.RevisionId = revision.Id;
        row.RevisionCommit = revision.Commit;
        row.LockPath = template.Build!.LockPath;
        row.AttemptCount = 0;
        (row.Sha256, row.Size, row.FileName, row.UploadAttemptId, row.Error, row.BuiltAt, row.DeletedAt) = (null, null, null, null, null, null, null);
    }

    /// <summary>A new attempt for the build, queued; the row points at it.</summary>
    private void QueueImageAttempt(OctopushyDb db, ImageBuild row)
    {
        var attempt = new Attempt
        {
            Id = Guid.CreateVersion7(),
            RevisionId = row.RevisionId,
            Number = row.AttemptCount + 1,
            Executor = TemplateImages.Executor,
            Action = TemplateImages.Action,
            Mutating = false,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Attempts.Add(attempt);
        row.AttemptId = attempt.Id;
        row.AttemptCount++;
        row.Status = ImageBuildStatuses.Queued;
    }

    /// <summary>The newest valid revision of the current source, read, or null when there is none or it cannot be read now.</summary>
    private async Task<(DefinitionRevision Revision, LabRepository Repository)?> NewestValidAsync(OctopushyDb db, CancellationToken cancellationToken)
    {
        var revision = await RepositoryTrust.EligibleRevisions(db).AsNoTracking().OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
        if (revision is null)
            return null;
        try
        {
            return (revision, await revisions.GetAsync(revision, cancellationToken));
        }
        catch (GitException e)
        {
            logger.LogWarning("The revision {Commit} cannot be read for its built templates: {Error}", revision.Commit, e.Message);
            return null;
        }
    }

    /// <summary>
    /// Hands a queued build to an agent: any connected one that advertises <c>nix</c>, is neither disabled nor draining (the
    /// caller's <paramref name="agents"/>), and has a free slot. Returns the message to send, or null when it waits.
    /// </summary>
    private async Task<(AgentSession Session, ServerMessage Message)?> DispatchImageBuildAsync(OctopushyDb db, Attempt attempt, List<AgentRecord> agents,
        Dictionary<Guid, int> perAgent, AssignmentBuilder builder, CancellationToken cancellationToken)
    {
        var build = await db.ImageBuilds.FirstOrDefaultAsync(b => b.AttemptId == attempt.Id, cancellationToken);
        if (build is null || !ImageBuildStatuses.InProgress(build.Status))
        {
            // Its build was queued again, or settled, since: this attempt is nobody's.
            attempt.Status = AttemptStatuses.Cancelled;
            attempt.Error = "The build this attempt was for was queued again or settled.";
            attempt.FinishedAt = clock.GetUtcNow();
            return null;
        }

        if (!await RepositoryTrust.EligibleRevisions(db).AnyAsync(r => r.Id == attempt.RevisionId, cancellationToken))
        {
            attempt.WaitingReason = "Waiting for a valid revision of the current repository source; the next sync queues it from there.";
            return null;
        }

        var candidates = agents.Where(a => connections.Get(a.Id) is { } s && s.Executors.Contains(TemplateImages.Executor)).ToList();
        var free = candidates.Where(a => perAgent.GetValueOrDefault(a.Id) < a.MaxConcurrency).ToList();
        if (candidates.Count == 0 || free.Count == 0)
        {
            attempt.WaitingReason = candidates.Count == 0
                ? $"Waiting for a connected agent advertising executor {TemplateImages.Executor}."
                : "Waiting for agent capacity.";
            return null;
        }

        var agent = free.MinBy(a => perAgent.GetValueOrDefault(a.Id))!;
        if (connections.Get(agent.Id) is not { } session)
        {
            attempt.WaitingReason = "The agent disconnected before assignment.";
            return null;
        }

        Assignment assignment;
        try
        {
            assignment = await builder.BuildImageAsync(attempt, build, agent.Id, cancellationToken);
        }
        catch (Exception e) when (e is AssignmentException or IOException or GitException)
        {
            logger.LogWarning("The image build {Key} cannot be assigned: {Error}", build.Key, e.Message);
            attempt.Status = AttemptStatuses.Failed;
            attempt.Error = e.Message;
            attempt.FinishedAt = clock.GetUtcNow();
            build.Status = ImageBuildStatuses.Failed;
            build.Error = e.Message;
            return null;
        }

        var now = clock.GetUtcNow();
        attempt.Status = AttemptStatuses.Assigned;
        attempt.AgentId = agent.Id;
        attempt.AssignedAt = now;
        attempt.LeaseExpiresAt = now.AddSeconds(options.Value.AgentChannel.LeaseSeconds);
        attempt.WaitingReason = null;
        build.Status = ImageBuildStatuses.Building;
        perAgent[agent.Id] = perAgent.GetValueOrDefault(agent.Id) + 1;
        return (session, new ServerMessage { Assignment = assignment });
    }

    /// <summary>
    /// Settles a build with its attempt's result, which the caller has recorded on the attempt. It is built only when the
    /// outputs name exactly the image this attempt uploaded; otherwise it failed. A built image proposes the provision runs that
    /// waited for it.
    /// </summary>
    private async Task FinishImageBuildAsync(OctopushyDb db, IServiceProvider services, Attempt attempt, JsonNode? outputs, CancellationToken cancellationToken)
    {
        var build = await db.ImageBuilds.FirstOrDefaultAsync(b => b.AttemptId == attempt.Id, cancellationToken);
        if (build is null || build.Status is not (ImageBuildStatuses.Building or ImageBuildStatuses.Queued))
            return;
        var now = clock.GetUtcNow();
        if (attempt.Status == AttemptStatuses.Succeeded)
        {
            var problem = UploadMismatch(build, attempt, outputs);
            if (problem is null)
            {
                build.Status = ImageBuildStatuses.Built;
                build.BuiltAt = build.LastUsedAt = now;
                build.Error = null;
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Built the image {Sha256} ({Size} bytes) of {Template}, key {Key}.", build.Sha256, build.Size, build.TemplateId, build.Key);
                if (await NewestValidAsync(db, cancellationToken) is { } newest)
                    await RetainImagesAsync(db, newest.Repository, cancellationToken);
                await ProposeWaitingGuestsAsync(db, services, cancellationToken);
                return;
            }

            attempt.Status = AttemptStatuses.Failed;
            attempt.Error = problem;
        }

        build.Status = ImageBuildStatuses.Failed;
        build.Error = attempt.Error ?? $"The build ended {attempt.Status}.";
        // What it uploaded is not its image: retention removes the file, unless another build names it.
        (build.Sha256, build.Size, build.FileName, build.UploadAttemptId) = (null, null, null, null);
        logger.LogWarning("The build of the image of {Template}, key {Key}, failed: {Error}", build.TemplateId, build.Key, build.Error);
        await db.SaveChangesAsync(cancellationToken);
        await RetainImagesAsync(db, (await NewestValidAsync(db, cancellationToken))?.Repository, cancellationToken);
    }

    /// <summary>Why a build's reported outputs do not name the image its attempt uploaded, or null when they do.</summary>
    internal string? UploadMismatch(ImageBuild build, Attempt attempt, JsonNode? outputs)
    {
        if (build.UploadAttemptId != attempt.Id || build.Sha256 is null)
            return "The build reported success, and its image was never uploaded whole.";
        string? Text(string name) => outputs?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        long? Number(string name) => outputs?[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;
        if (Text("sha256")?.ToLowerInvariant() != build.Sha256 || Number("size") != build.Size || Text("fileName") != build.FileName
            || string.IsNullOrEmpty(Text("outPath")))
            return "The build's outputs (sha256, size, fileName, outPath) do not match the image it uploaded.";
        return images.Has(build.Sha256) ? null : "The uploaded image is no longer on the server.";
    }

    /// <summary>The provision runs that waited for an image, proposed from the head revision when it is valid and applied.</summary>
    private async Task ProposeWaitingGuestsAsync(OctopushyDb db, IServiceProvider services, CancellationToken cancellationToken)
    {
        var head = await RepositoryTrust.HeadRevisions(db).AsNoTracking().Where(r => r.RepositoryValid && r.InventoryAppliedAt != null)
            .OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
        if (head is null)
            return;
        LabRepository repository;
        try
        {
            repository = await revisions.GetAsync(head, cancellationToken);
        }
        catch (GitException e)
        {
            logger.LogWarning("Runs waiting for an image cannot be proposed now: {Error}", e.Message);
            return;
        }

        var proposed = await services.GetRequiredService<InventoryUpdater>().ProposeWaitingAsync(db, head, repository, cancellationToken);
        if (proposed > 0)
            logger.LogInformation("Proposed {Count} provision run(s) that waited for an image.", proposed);
    }

    /// <summary>A build attempt whose agent stopped reporting: tried again, as a read-only step is, then failed.</summary>
    private async Task ExpireImageBuildAsync(OctopushyDb db, Attempt attempt, CancellationToken cancellationToken)
    {
        images.Release(attempt.Id);
        var build = await db.ImageBuilds.FirstOrDefaultAsync(b => b.AttemptId == attempt.Id, cancellationToken);
        if (build is null || !ImageBuildStatuses.InProgress(build.Status))
            return;
        // A half-sent image is discarded with its partial file; one sent whole stays on disk until retention finds it unused.
        if (build.UploadAttemptId == attempt.Id)
            (build.Sha256, build.Size, build.FileName, build.UploadAttemptId) = (null, null, null, null);
        if (build.AttemptCount <= options.Value.Scheduling.ReadOnlyRetries)
        {
            QueueImageAttempt(db, build);
            return;
        }

        build.Status = ImageBuildStatuses.Failed;
        build.Error = $"The agent stopped reporting, {build.AttemptCount} times.";
        logger.LogWarning("The build of the image of {Template}, key {Key}, failed: {Error}", build.TemplateId, build.Key, build.Error);
    }

    /// <summary>
    /// Whether an agent may upload an image for the attempt: a <c>nix</c> <c>build</c> attempt assigned to it, under way, whose
    /// build is building. Then makes room for <paramref name="size"/> bytes with retention, and holds it until the upload ends;
    /// an image that would not fit fails the build with "image store full", and nothing referenced is deleted for it.
    /// </summary>
    public Task AuthorizeImageUploadAsync(Guid agentId, Guid attemptId, string fileName, long size, CancellationToken cancellationToken) =>
        WithDbAsync(async (db, _) =>
        {
            var attempt = await db.Attempts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attemptId, cancellationToken);
            if (attempt is null || attempt.AgentId != agentId || !TemplateImages.IsBuild(attempt) || !AttemptStatuses.IsActive(attempt.Status))
                throw new ImageAccessException("This attempt is not a build assigned to this agent and under way.");
            var build = await db.ImageBuilds.FirstOrDefaultAsync(b => b.AttemptId == attemptId, cancellationToken);
            if (build?.Status != ImageBuildStatuses.Building)
                throw new ImageAccessException("This attempt's build is not building.");
            if (!ImageStore.IsPlainFileName(fileName))
                throw new ImageUploadException("The image's file name must be a plain name: letters, digits, '.', '_' and '-'.");
            if (size <= 0)
                throw new ImageUploadException("An image has a size.");

            var fits = size <= images.MaxBytes;
            if (fits)
            {
                var repository = (await NewestValidAsync(db, cancellationToken))?.Repository;
                fits = (await RetainImagesAsync(db, repository, cancellationToken, size + images.Reserved(attemptId))).Fits;
            }

            if (!fits)
            {
                build.Status = ImageBuildStatuses.Failed;
                build.Error = "image store full";
                await db.SaveChangesAsync(cancellationToken);
                logger.LogWarning("The image of {Template}, key {Key}, of {Size} bytes, does not fit: image store full.", build.TemplateId, build.Key, size);
                throw new ImageUploadException("image store full", full: true);
            }

            images.Reserve(attemptId, size);
            return 0;
        }, cancellationToken);

    /// <summary>
    /// Records an upload received whole: its file moves into place, and the build names it until its result says whether that is
    /// the image. Refused, and the file discarded, if the attempt is no longer the agent's build under way.
    /// </summary>
    public Task CompleteImageUploadAsync(Guid agentId, Guid attemptId, string sha256, long size, string fileName, CancellationToken cancellationToken) =>
        WithDbAsync(async (db, _) =>
        {
            var attempt = await db.Attempts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attemptId, cancellationToken);
            var build = await db.ImageBuilds.FirstOrDefaultAsync(b => b.AttemptId == attemptId, cancellationToken);
            if (attempt is null || attempt.AgentId != agentId || !AttemptStatuses.IsActive(attempt.Status) || build?.Status != ImageBuildStatuses.Building)
            {
                images.Release(attemptId);
                throw new ImageAccessException("This attempt's build stopped while its image was sent.");
            }

            images.Commit(attemptId, sha256);
            (build.Sha256, build.Size, build.FileName, build.UploadAttemptId) = (sha256, size, fileName, attemptId);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Received the image {Sha256} ({Size} bytes) of {Template} from attempt {Attempt}.", sha256, size, build.TemplateId, attemptId);
            return 0;
        }, cancellationToken);

    /// <summary>
    /// The image an agent may fetch for an attempt: one assigned to it and under way, whose OpenTofu input names exactly this image
    /// as its <c>templateImage</c>. So an agent can fetch only what the work it holds needs.
    /// </summary>
    public Task<TemplateImage> AuthorizeImageDownloadAsync(Guid agentId, Guid attemptId, string sha256, CancellationToken cancellationToken) =>
        WithDbAsync(async (db, _) =>
        {
            var attempt = await db.Attempts.AsNoTracking().Include(a => a.Step).ThenInclude(s => s!.Run)
                .FirstOrDefaultAsync(a => a.Id == attemptId, cancellationToken);
            if (attempt?.Step?.Run is not { } run || attempt.AgentId != agentId || !AttemptStatuses.IsActive(attempt.Status)
                || attempt.Executor != "opentofu"
                || RunContext.Parse(run.Context).ForStep(attempt.Step).OpenTofu?.TemplateImage is not { } image || image.Sha256 != sha256)
                throw new ImageAccessException("This attempt is not assigned to this agent, under way, and in need of this image.");
            await db.ImageBuilds.Where(b => b.Sha256 == sha256 && b.Status == ImageBuildStatuses.Built)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.LastUsedAt, clock.GetUtcNow()), cancellationToken);
            return image;
        }, cancellationToken);

    /// <summary>
    /// Applies retention: the images that a built template of <paramref name="repository"/> resolves to, that an unfinished
    /// run needs, or whose build is under way, stay; of the rest, <c>KeepUnreferenced</c> per template, the newest, stay while
    /// the total is within <c>MaxBytes</c>, counting <paramref name="incoming"/> bytes about to arrive. Deleted rows are marked
    /// so, and a file no row keeps is removed. Inside the engine's gate.
    /// </summary>
    private async Task<RetentionPlan> RetainImagesAsync(OctopushyDb db, LabRepository? repository, CancellationToken cancellationToken, long incoming = 0)
    {
        var rows = await db.ImageBuilds.Where(b => b.Sha256 != null && b.Status != ImageBuildStatuses.Deleted && b.Status != ImageBuildStatuses.Failed)
            .ToListAsync(cancellationToken);
        // An image whose file went from under the server, as when its folder was emptied by hand, is as good as deleted: its key is
        // built again by the next sync that needs it.
        foreach (var lost in rows.Where(r => r.Status == ImageBuildStatuses.Built && !images.Has(r.Sha256!)).ToList())
        {
            logger.LogWarning("The image {Sha256} of {Template}, key {Key}, is missing from {Directory}; it will be built again.", lost.Sha256, lost.TemplateId,
                lost.Key, images.Directory);
            lost.Status = ImageBuildStatuses.Deleted;
            lost.DeletedAt = clock.GetUtcNow();
            rows.Remove(lost);
        }

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var keys = repository?.Templates.Values.Select(t => t.Build?.Key).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        // Without a revision to read, everything built is taken as referenced: nothing is deleted for want of knowing.
        foreach (var row in rows.Where(r => repository is null || keys.Contains(r.Key) || r.Status != ImageBuildStatuses.Built))
            referenced.Add(row.Sha256!);
        var contexts = await db.Runs.AsNoTracking()
            .Where(r => r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
            .Select(r => r.Context).ToListAsync(cancellationToken);
        foreach (var context in contexts)
        {
            if (RunContext.Parse(context).OpenTofu?.TemplateImage is { } image)
                referenced.Add(image.Sha256);
        }

        var stored = rows.Select(r => new StoredImage(r.Id, r.TemplateId, r.Sha256!, r.Size ?? 0, r.BuiltAt ?? r.CreatedAt)).ToList();
        var plan = ImageRetention.Plan(stored, referenced, images.KeepUnreferenced, images.MaxBytes, incoming);
        var now = clock.GetUtcNow();
        foreach (var deleted in plan.Delete)
        {
            var row = rows.Single(r => r.Id == deleted.Id);
            row.Status = ImageBuildStatuses.Deleted;
            row.DeletedAt = now;
            logger.LogInformation("Retention deleted the image {Sha256} ({Size} bytes) of {Template}, key {Key}.", row.Sha256, row.Size, row.TemplateId, row.Key);
        }

        await db.SaveChangesAsync(cancellationToken);
        // A file is kept while a row that is not deleted names it, or a run needs it. Anything else in the folder, such as an image a
        // failed build uploaded or one whose row was deleted, goes; uploads under way are in .partial, which this never reads.
        var named = (await db.ImageBuilds.AsNoTracking().Where(b => b.Sha256 != null && b.Status != ImageBuildStatuses.Deleted)
            .Select(b => b.Sha256!).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var file in images.Files().Where(f => !named.Contains(f) && !referenced.Contains(f)))
            images.Delete(file);
        return plan;
    }
}
