using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Server.Data;
using Octopushy.Server.Options;
using Octopushy.Server.Store;

namespace Octopushy.Server.Notifications;

/// <summary>
/// Sends notifications to the webhook, one at a time, in id order, from where <see cref="WebhookState.Position"/> says it
/// stopped. It never takes the engine's lock and never runs inside a save: it reads <c>notifications</c> after they are
/// committed, and writes only the one <c>webhook</c> row, with conditional bulk updates the engine does not count as saves.
/// <para>
/// A delivery is a 2xx answer. A 410 stops sending until the URL is set again. Anything else is tried again after
/// <see cref="Backoff"/>; an event is given up once it is older than <see cref="MaxAge"/>, or after <see cref="RefusedAttempts"/>
/// answers that refuse it (a 4xx other than 408 and 429), and when more than <see cref="MaxBacklog"/> wait, the oldest go.
/// A delivery may be repeated after a restart, never lost to one: the event id lets a receiver drop the repeat.
/// </para>
/// </summary>
public sealed class WebhookDelivery(
    IServiceScopeFactory scopes,
    WebhookTransport transport,
    WebhookSignal signal,
    IOptions<OctopushyOptions> options,
    TimeProvider clock,
    ILogger<WebhookDelivery> logger) : BackgroundService
{
    /// <summary>How long the sender waits when nothing woke it. Saves that add a notification wake it at once.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(1);

    /// <summary>An event older than this is dropped, not sent.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>The most events that wait to be sent; older ones beyond it are dropped, so a receiver back after a day is not flooded.</summary>
    public const int MaxBacklog = 50;

    /// <summary>How many refusals of one event (a 4xx other than 408, 410 and 429) give it up.</summary>
    public const int RefusedAttempts = 5;

    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(10);

    /// <summary>How long the sender waits for notifications being added to commit before it gives up a round.</summary>
    public const int LockTimeoutSeconds = 1;

    private const string SetLockTimeout = "SET LOCAL lock_timeout = '1s'";

    /// <summary>The wait after a round given up because notifications were being added: they wake the sender once committed.</summary>
    public static readonly TimeSpan Busy = TimeSpan.FromSeconds(10);

    private sealed record Bounded(Notification? Next, (long Last, int Count)? Backlog);

    /// <summary>
    /// The next notification to send and, when the oldest waiting is older than <paramref name="backlogBefore"/>, the waiting
    /// ones past <see cref="MaxBacklog"/>, read behind a gapless boundary. An id is taken by the INSERT that adds its row, after
    /// that statement holds ROW EXCLUSIVE on <c>notifications</c>, and its sequence hands out one value at a time. SHARE
    /// conflicts with ROW EXCLUSIVE, so once it is held every transaction that took an id has committed or rolled back, and any
    /// id taken later is higher than every id read here: a notification committed after a higher one is never passed over.
    /// <para>
    /// The lock is held only for these reads, in a transaction of its own that takes no row lock and holds no other lock while it
    /// waits, so it cannot deadlock with the engine. A waiting SHARE request queues new inserts behind it, so it waits at most
    /// <see cref="LockTimeoutSeconds"/>: null when it gave up, having read and changed nothing.
    /// </para>
    /// </summary>
    private static async Task<Bounded?> BoundedAsync(OctopushyDb db, IQueryable<Notification> waiting, DateTimeOffset? backlogBefore, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlRawAsync(SetLockTimeout, ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("LOCK TABLE notifications IN SHARE MODE", ct);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == Npgsql.PostgresErrorCodes.LockNotAvailable)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return null;
        }

        // Read committed: each read below sees every row committed before the lock was granted.
        var next = await waiting.OrderBy(n => n.Id).FirstOrDefaultAsync(ct);
        (long, int)? backlog = null;
        if (next is not null && backlogBefore is { } before && next.CreatedAt < before
            && await waiting.OrderByDescending(n => n.Id).Skip(MaxBacklog).Select(n => (long?)n.Id).FirstOrDefaultAsync(ct) is { } last)
            backlog = (last, await waiting.CountAsync(n => n.Id <= last, ct));
        await transaction.CommitAsync(ct);
        return new Bounded(next, backlog);
    }

    /// <summary>The wait after the <paramref name="attempts"/>th failure: 10 seconds, doubled each time, at most 10 minutes.</summary>
    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromTicks(Math.Min(MaxRetry.Ticks, FirstRetry.Ticks << Math.Clamp(attempts - 1, 0, 16)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = await SendNextAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // Database errors only: the URL never reaches an exception this code throws.
                logger.LogWarning(e, "Sending the webhook's next event failed; it is tried again.");
                wait = FirstRetry;
            }

            if (wait <= TimeSpan.Zero)
                continue;
            try
            {
                await signal.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Sends the next event when one is due, and records what came of it. Returns how long to wait before the next call: zero
    /// when another may be due at once.
    /// </summary>
    public async Task<TimeSpan> SendNextAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        var state = await db.Webhooks.AsNoTracking().FirstOrDefaultAsync(w => w.Id == 1, ct);
        if (state is not { Enabled: true })
            return Idle;
        var now = clock.GetUtcNow();
        if (state.NextAttemptAt is { } due && due > now)
            return due - now < Idle ? due - now : Idle;
        var kinds = state.Kinds;
        var waiting = db.Notifications.AsNoTracking().Where(n => n.Id > state.Position && kinds.Contains(n.Kind));
        // Without a lock first: nothing to send is the common case, and costs one read. A notification committed later wakes
        // this sender.
        if (!await waiting.AnyAsync(ct))
            return Idle;
        if (await BoundedAsync(db, waiting, state.Attempts == 0 ? now - Idle : null, ct) is not { } read)
        {
            logger.LogInformation("The webhook waited {Seconds} second for notifications being added; it tries again later.", LockTimeoutSeconds);
            return Busy;
        }

        if (read.Next is not { } next)
            return Idle;

        // Settled for this webhook, as it was when read: a change of settings meanwhile makes this record nothing.
        var mine = db.Webhooks.Where(w => w.Id == 1 && w.Generation == state.Generation && w.Position == state.Position);
        if (read.Backlog is { } backlog)
        {
            await mine.ExecuteUpdateAsync(u => u.SetProperty(w => w.Position, backlog.Last).SetProperty(w => w.Dropped, w => w.Dropped + backlog.Count)
                .SetProperty(w => w.Attempts, 0).SetProperty(w => w.Refusals, 0), ct);
            logger.LogWarning("The webhook dropped {Count} waiting events, keeping the latest {Kept}.", backlog.Count, MaxBacklog);
            return TimeSpan.Zero;
        }

        if (now - next.CreatedAt > MaxAge)
        {
            await mine.ExecuteUpdateAsync(u => u.SetProperty(w => w.Position, next.Id).SetProperty(w => w.Attempts, 0).SetProperty(w => w.Refusals, 0)
                .SetProperty(w => w.NextAttemptAt, (DateTimeOffset?)null).SetProperty(w => w.Dropped, w => w.Dropped + 1), ct);
            logger.LogWarning("The webhook dropped event {Event}: older than {Hours} hours.", next.Id, (int)MaxAge.TotalHours);
            return TimeSpan.Zero;
        }

        if (await Webhook.TargetAsync(scope.ServiceProvider.GetRequiredService<VariableStore>(), ct) is not { } target)
            return Idle;
        var body = await Webhook.PayloadAsync(db, next, options.Value.PublicUrl, ct);
        var answer = await transport.PostAsync(target.Url, target.Secret, next.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            body, now.ToUnixTimeSeconds(), ct);
        var at = clock.GetUtcNow();
        var attempts = state.Attempts + 1;
        var refusals = state.Refusals + (answer.Refused ? 1 : 0);
        if (answer.Delivered)
        {
            await mine.ExecuteUpdateAsync(u => u.SetProperty(w => w.Position, next.Id).SetProperty(w => w.Attempts, 0).SetProperty(w => w.Refusals, 0)
                .SetProperty(w => w.NextAttemptAt, (DateTimeOffset?)null).SetProperty(w => w.LastAttemptAt, at)
                .SetProperty(w => w.LastEventId, next.Id).SetProperty(w => w.LastDelivered, true).SetProperty(w => w.LastStatus, answer.Status)
                .SetProperty(w => w.LastResult, answer.Result).SetProperty(w => w.LastDeliveredAt, at), ct);
            return TimeSpan.Zero;
        }

        if (answer.Gone)
        {
            await mine.ExecuteUpdateAsync(u => u.SetProperty(w => w.Enabled, false).SetProperty(w => w.StoppedAt, at)
                .SetProperty(w => w.Attempts, 0).SetProperty(w => w.Refusals, 0).SetProperty(w => w.NextAttemptAt, (DateTimeOffset?)null).SetProperty(w => w.LastAttemptAt, at)
                .SetProperty(w => w.LastEventId, next.Id).SetProperty(w => w.LastDelivered, false).SetProperty(w => w.LastStatus, answer.Status)
                .SetProperty(w => w.LastResult, answer.Result), ct);
            logger.LogWarning("The webhook's receiver answered 410 Gone; sending stopped until the URL is set again.");
            return Idle;
        }

        if (refusals >= RefusedAttempts)
        {
            await mine.ExecuteUpdateAsync(u => u.SetProperty(w => w.Position, next.Id).SetProperty(w => w.Attempts, 0).SetProperty(w => w.Refusals, 0)
                .SetProperty(w => w.NextAttemptAt, (DateTimeOffset?)null).SetProperty(w => w.Dropped, w => w.Dropped + 1)
                .SetProperty(w => w.LastAttemptAt, at).SetProperty(w => w.LastEventId, next.Id).SetProperty(w => w.LastDelivered, false)
                .SetProperty(w => w.LastStatus, answer.Status).SetProperty(w => w.LastResult, answer.Result), ct);
            logger.LogWarning("The webhook dropped event {Event}: the receiver refused it {Refusals} times ({Status}).", next.Id, refusals, answer.Status);
            return TimeSpan.Zero;
        }

        var retry = Backoff(attempts);
        await mine.ExecuteUpdateAsync(u => u.SetProperty(w => w.Attempts, attempts).SetProperty(w => w.Refusals, refusals).SetProperty(w => w.NextAttemptAt, at + retry)
            .SetProperty(w => w.LastAttemptAt, at).SetProperty(w => w.LastEventId, next.Id).SetProperty(w => w.LastDelivered, false)
            .SetProperty(w => w.LastStatus, answer.Status).SetProperty(w => w.LastResult, answer.Result), ct);
        logger.LogInformation("Sending webhook event {Event} failed ({Result}); it is tried again in {Seconds} seconds.", next.Id, answer.Result, (int)retry.TotalSeconds);
        return retry;
    }
}
