using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Octopushy.Server.Api;
using Octopushy.Server.Changes;
using Octopushy.Server.Data;
using Octopushy.Server.Uptime;

namespace Octopushy.Server.Notifications;

/// <summary>
/// The one place that tells an operator something happened. A notification is a row of <c>notifications</c>, added to the
/// save that makes the change it reports, so it exists exactly when the change does. The web app reads the rows for its
/// pop-ups and its list. See docs/notifications.md, which also says how a provider such as Telegram would read them.
/// <para>
/// As an interceptor it reports every run that a save moves to failed, uncertain or awaiting approval, whoever moved it.
/// A run that succeeds is not told. It writes no run, step or attempt.
/// </para>
/// </summary>
public sealed class NotificationHub(TimeProvider clock, WebhookSignal? published = null) : SaveChangesInterceptor
{
    /// <summary>How many of the latest notifications the API lists.</summary>
    public const int Listed = 50;

    private static void Publish(OctopushyDb db, string kind, string title, string? body, Guid? resourceId, Guid? runId, DateTimeOffset at,
        Guid? agentId = null) =>
        db.Notifications.Add(new Notification
        {
            Kind = kind,
            Title = ChangeComposer.Truncate(title, 200),
            Body = string.IsNullOrEmpty(body) ? null : ChangeComposer.Truncate(body, 500),
            ResourceId = resourceId,
            RunId = runId,
            AgentId = agentId,
            CreatedAt = at,
        });

    /// <summary>
    /// An agent turned offline or online, as <see cref="Agents.AgentPresence"/> decides. The caller saves, together with the
    /// agent's <see cref="AgentRecord.OfflineNotifiedAt"/>.
    /// </summary>
    public static void AgentTurned(OctopushyDb db, AgentRecord agent, bool online, DateTimeOffset at) =>
        Publish(db, online ? NotificationKinds.AgentOnline : NotificationKinds.AgentOffline,
            online ? $"Agent {agent.Name} is connected again" : $"Agent {agent.Name} is offline", null, null, null, at, agent.Id);

    /// <summary>
    /// An agent's update failed. The caller saves, together with the failed state. Why it failed is on the Agents page, not
    /// here: it may quote a change's detail.
    /// </summary>
    public static void AgentUpdateFailed(OctopushyDb db, AgentRecord agent, DateTimeOffset at) =>
        Publish(db, NotificationKinds.AgentUpdateFailed, $"Update of agent {agent.Name} failed", null, null, null, at, agent.Id);

    /// <summary>An uptime check that answered up now answers down, or the other way round. The caller saves.</summary>
    public static async Task CheckTurnedAsync(OctopushyDb db, UptimeTarget target, bool up, string? detail, DateTimeOffset at, CancellationToken ct)
    {
        var address = target.Resource ?? target.Installation;
        var resource = await db.Resources.Where(r => r.Address == address && r.RetiredAt == null).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        if (up)
            Publish(db, NotificationKinds.CheckUp, $"{address} is up again", target.Url, resource, null, at);
        else
            Publish(db, NotificationKinds.CheckDown, $"{address} is down", string.IsNullOrEmpty(detail) ? target.Url : $"{target.Url} · {detail}", resource, null, at);
    }

    /// <summary>The runs this save moves to a status in <see cref="Told"/>, less those a failed save already reported.</summary>
    private static List<Run> Turned(OctopushyDb db)
    {
        var reported = db.ChangeTracker.Entries<Notification>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        return db.ChangeTracker.Entries<Run>()
            .Where(e => Told.ContainsKey(e.Entity.Status)
                && (e.State == EntityState.Added
                    || e.State == EntityState.Modified && e.Property(r => r.Status).OriginalValue != e.Entity.Status)
                && !reported.Any(n => n.RunId == e.Entity.Id && n.Kind == KindOf(e.Entity)))
            .Select(e => e.Entity).ToList();
    }

    /// <summary>The statuses of a run that are told, each with its kind and how its title ends.</summary>
    private static readonly Dictionary<string, (string Kind, string Ending)> Told = new()
    {
        [RunStatuses.Failed] = (NotificationKinds.RunFailed, "failed"),
        [RunStatuses.Uncertain] = (NotificationKinds.RunUncertain, "has an uncertain outcome"),
        [RunStatuses.AwaitingApproval] = (NotificationKinds.RunApproval, "awaits approval"),
    };

    private static string KindOf(Run run) => Told[run.Status].Kind;

    private void Publish(OctopushyDb db, Run run, Resource? resource)
    {
        var operation = char.ToUpperInvariant(run.Operation[0]) + run.Operation[1..];
        Publish(db, KindOf(run), $"{operation} run {Told[run.Status].Ending}", resource?.Address, resource?.Id, run.Id, run.FinishedAt ?? clock.GetUtcNow());
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is OctopushyDb db)
        {
            foreach (var run in Turned(db))
                Publish(db, run, db.Resources.Find(run.ResourceId));
            NoteAdding(db);
        }

        return result;
    }

    /// <summary>The contexts whose save under way adds a notification.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbContext, object> _adding = [];

    private void NoteAdding(OctopushyDb db)
    {
        if (published is not null && db.ChangeTracker.Entries<Notification>().Any(e => e.State == EntityState.Added))
            _adding.AddOrUpdate(db, published);
    }

    // A save that added a notification wakes the webhook's sender once it is committed.
    private void Saved(DbContext? context)
    {
        if (context is not null && _adding.Remove(context))
            published?.Wake();
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Saved(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Saved(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => _adding.Remove(eventData.Context!);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _adding.Remove(eventData.Context!);
        return Task.CompletedTask;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is OctopushyDb db)
        {
            foreach (var run in Turned(db))
                Publish(db, run, await db.Resources.FindAsync([run.ResourceId], cancellationToken));
            NoteAdding(db);
        }

        return result;
    }

    /// <summary>The most one page of the list may hold.</summary>
    public const int MaxListed = 200;

    /// <summary>
    /// Lists notifications newest first, a page at a time. With no parameters it is the latest <see cref="Listed"/>, which the
    /// bell reads. <c>before</c> and <c>after</c> bound the ids, <c>kind</c> keeps the kinds named (repeat it for more than one),
    /// and <c>q</c> keeps those whose title or body holds the text, ignoring case. <c>NextBefore</c> is the id to pass as
    /// <c>before</c> for the next page, or null when there is none.
    /// </summary>
    public static void MapNotifications(RouteGroupBuilder api)
    {
        api.MapGet("/notifications", async Task<Results<Ok<NotificationsDto>, ProblemHttpResult>> (OctopushyDb db, long? before, long? after,
            int? limit, string[]? kind, string? q, CancellationToken ct) =>
        {
            var take = limit ?? Listed;
            if (take is < 1 or > MaxListed)
                return TypedResults.Problem(title: $"List between 1 and {MaxListed} notifications at a time.", statusCode: 400);
            var kinds = kind?.Where(k => !string.IsNullOrEmpty(k)).Distinct().ToArray() ?? [];
            if (kinds.Any(k => !NotificationKinds.All.Contains(k)))
                return TypedResults.Problem(title: $"A kind is one of {string.Join(", ", NotificationKinds.All)}.", statusCode: 400);
            var text = q?.Trim();
            if (text is { Length: > 200 })
                return TypedResults.Problem(title: "Search for at most 200 characters.", statusCode: 400);

            var query = db.Notifications.AsNoTracking();
            if (before is not null)
                query = query.Where(n => n.Id < before);
            if (after is not null)
                query = query.Where(n => n.Id > after);
            if (kinds.Length > 0)
                query = query.Where(n => kinds.Contains(n.Kind));
            if (!string.IsNullOrEmpty(text))
            {
                // Contains is strpos, not LIKE, so the text's % and _ match themselves.
                var lower = text.ToLowerInvariant();
                query = query.Where(n => n.Title.ToLower().Contains(lower) || (n.Body != null && n.Body.ToLower().Contains(lower)));
            }

            // One more than a page tells whether another page follows.
            var items = await query.OrderByDescending(n => n.Id).Take(take + 1)
                .Select(n => new NotificationDto(n.Id, n.Kind, n.Title, n.Body, n.ResourceId,
                    db.Resources.Where(r => r.Id == n.ResourceId).Select(r => r.Kind).FirstOrDefault(), n.RunId, n.CreatedAt, n.AgentId,
                    db.Runs.Where(r => r.Id == n.RunId).Select(r => (long?)r.Number).FirstOrDefault(),
                    db.Resources.Where(r => r.Id == n.ResourceId).Select(r => r.Address).FirstOrDefault(),
                    db.Agents.Where(a => a.Id == n.AgentId).Select(a => a.Name).FirstOrDefault()))
                .ToListAsync(ct);
            var more = items.Count > take;
            if (more)
                items.RemoveAt(take);
            return TypedResults.Ok(new NotificationsDto(items, more ? items[^1].Id : null));
        })
            .WithTags("Notifications")
            .WithName("listNotifications")
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }
}
