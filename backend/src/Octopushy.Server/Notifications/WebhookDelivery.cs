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
        var next = await waiting.OrderBy(n => n.Id).FirstOrDefaultAsync(ct);
        if (next is null)
            return Idle;

        // Settled for this webhook, as it was when read: a change of settings meanwhile makes this record nothing.
        var mine = db.Webhooks.Where(w => w.Id == 1 && w.Generation == state.Generation && w.Position == state.Position);
        if (state.Attempts == 0 && now - next.CreatedAt > Idle
            && await waiting.OrderByDescending(n => n.Id).Skip(MaxBacklog).Select(n => (long?)n.Id).FirstOrDefaultAsync(ct) is { } last)
        {
            var dropped = await waiting.CountAsync(n => n.Id <= last, ct);
            await mine.ExecuteUpdateAsync(u => u.SetProperty(w => w.Position, last).SetProperty(w => w.Dropped, w => w.Dropped + dropped), ct);
            logger.LogWarning("The webhook dropped {Count} waiting events, keeping the latest {Kept}.", dropped, MaxBacklog);
            return TimeSpan.Zero;
        }

        if (now - next.CreatedAt > MaxAge)
        {
            await mine.ExecuteUpdateAsync(u => u.SetProperty(w => w.Position, next.Id).SetProperty(w => w.Attempts, 0)
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
        if (answer.Delivered)
        {
            await mine.ExecuteUpdateAsync(u => u.SetProperty(w => w.Position, next.Id).SetProperty(w => w.Attempts, 0)
                .SetProperty(w => w.NextAttemptAt, (DateTimeOffset?)null).SetProperty(w => w.LastAttemptAt, at)
                .SetProperty(w => w.LastEventId, next.Id).SetProperty(w => w.LastDelivered, true).SetProperty(w => w.LastStatus, answer.Status)
                .SetProperty(w => w.LastResult, answer.Result).SetProperty(w => w.LastDeliveredAt, at), ct);
            return TimeSpan.Zero;
        }

        if (answer.Gone)
        {
            await mine.ExecuteUpdateAsync(u => u.SetProperty(w => w.Enabled, false).SetProperty(w => w.StoppedAt, at)
                .SetProperty(w => w.Attempts, 0).SetProperty(w => w.NextAttemptAt, (DateTimeOffset?)null).SetProperty(w => w.LastAttemptAt, at)
                .SetProperty(w => w.LastEventId, next.Id).SetProperty(w => w.LastDelivered, false).SetProperty(w => w.LastStatus, answer.Status)
                .SetProperty(w => w.LastResult, answer.Result), ct);
            logger.LogWarning("The webhook's receiver answered 410 Gone; sending stopped until the URL is set again.");
            return Idle;
        }

        if (answer.Refused && attempts >= RefusedAttempts)
        {
            await mine.ExecuteUpdateAsync(u => u.SetProperty(w => w.Position, next.Id).SetProperty(w => w.Attempts, 0)
                .SetProperty(w => w.NextAttemptAt, (DateTimeOffset?)null).SetProperty(w => w.Dropped, w => w.Dropped + 1)
                .SetProperty(w => w.LastAttemptAt, at).SetProperty(w => w.LastEventId, next.Id).SetProperty(w => w.LastDelivered, false)
                .SetProperty(w => w.LastStatus, answer.Status).SetProperty(w => w.LastResult, answer.Result), ct);
            logger.LogWarning("The webhook dropped event {Event}: the receiver refused it {Attempts} times ({Status}).", next.Id, attempts, answer.Status);
            return TimeSpan.Zero;
        }

        var retry = Backoff(attempts);
        await mine.ExecuteUpdateAsync(u => u.SetProperty(w => w.Attempts, attempts).SetProperty(w => w.NextAttemptAt, at + retry)
            .SetProperty(w => w.LastAttemptAt, at).SetProperty(w => w.LastEventId, next.Id).SetProperty(w => w.LastDelivered, false)
            .SetProperty(w => w.LastStatus, answer.Status).SetProperty(w => w.LastResult, answer.Result), ct);
        logger.LogInformation("Sending webhook event {Event} failed ({Result}); it is tried again in {Seconds} seconds.", next.Id, answer.Result, (int)retry.TotalSeconds);
        return retry;
    }
}
