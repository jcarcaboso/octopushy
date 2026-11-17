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
public sealed class NotificationHub(TimeProvider clock) : SaveChangesInterceptor
{
    /// <summary>How many of the latest notifications the API lists.</summary>
    public const int Listed = 50;

    private static void Publish(OctopushyDb db, string kind, string title, string? body, Guid? resourceId, Guid? runId, DateTimeOffset at) =>
        db.Notifications.Add(new Notification
        {
            Kind = kind,
            Title = ChangeComposer.Truncate(title, 200),
            Body = string.IsNullOrEmpty(body) ? null : ChangeComposer.Truncate(body, 500),
            ResourceId = resourceId,
            RunId = runId,
            CreatedAt = at,
        });

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
            foreach (var run in Turned(db))
                Publish(db, run, db.Resources.Find(run.ResourceId));
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is OctopushyDb db)
            foreach (var run in Turned(db))
                Publish(db, run, await db.Resources.FindAsync([run.ResourceId], cancellationToken));
        return result;
    }

    public static void MapNotifications(RouteGroupBuilder api)
    {
        api.MapGet("/notifications", async Task<Ok<NotificationsDto>> (OctopushyDb db, CancellationToken ct) =>
            TypedResults.Ok(new NotificationsDto(await db.Notifications.AsNoTracking().OrderByDescending(n => n.Id).Take(Listed)
                .Select(n => new NotificationDto(n.Id, n.Kind, n.Title, n.Body, n.ResourceId,
                    db.Resources.Where(r => r.Id == n.ResourceId).Select(r => r.Kind).FirstOrDefault(), n.RunId, n.CreatedAt))
                .ToListAsync(ct))))
            .WithTags("Notifications")
            .WithName("listNotifications");
    }
}
