using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Options;
using Octopushy.Server.Store;

namespace Octopushy.Server.Notifications;

/// <summary>The webhook as the API shows it: whether a URL is set and its host, never the URL itself.</summary>
public sealed record WebhookDto(
    bool Configured,
    string? Host,
    bool Enabled,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> DefaultKinds,
    IReadOnlyList<string> AvailableKinds,
    bool HttpAllowed,
    bool LinksIncluded,
    DateTimeOffset? LastAttemptAt,
    bool? LastDelivered,
    int? LastStatus,
    string? LastResult,
    DateTimeOffset? LastDeliveredAt,
    DateTimeOffset? NextAttemptAt,
    long Dropped,
    DateTimeOffset? StoppedAt);

/// <summary>A change of the webhook. A URL sets it and makes a new signing secret; no URL keeps the one set and changes only the kinds.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WebhookUpdate(string? Url, IReadOnlyList<string>? Kinds);

/// <summary>The webhook once saved, and the signing secret when a URL was set: shown only now.</summary>
public sealed record WebhookSaved(WebhookDto Webhook, string? Secret);

/// <summary>What the test event came to.</summary>
public sealed record WebhookTestResult(bool Delivered, int? Status, string Result);

public static class WebhookEndpoints
{
    public const string TestRateLimit = "webhook-test";

    private static WebhookDto Dto(WebhookState? state, OctopushyOptions options) => new(
        state?.Host is not null,
        state?.Host,
        state?.Enabled ?? false,
        state?.Kinds ?? [.. Webhook.DefaultKinds],
        Webhook.DefaultKinds,
        NotificationKinds.All,
        options.Notifications.AllowHttpWebhook,
        !string.IsNullOrWhiteSpace(options.PublicUrl),
        state?.LastAttemptAt,
        state?.LastAttemptAt is null ? null : state.LastDelivered,
        state?.LastStatus,
        state?.LastResult,
        state?.LastDeliveredAt,
        state?.NextAttemptAt,
        state?.Dropped ?? 0,
        state?.StoppedAt);

    public static void MapWebhook(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/notifications/webhook").WithTags("Notifications");

        group.MapGet("", async (OctopushyDb db, IOptions<OctopushyOptions> options, CancellationToken ct) =>
            TypedResults.Ok(Dto(await db.Webhooks.AsNoTracking().FirstOrDefaultAsync(w => w.Id == 1, ct), options.Value)))
            .WithName("getWebhook");

        group.MapPut("", async Task<Results<Ok<WebhookSaved>, ProblemHttpResult>> (WebhookUpdate update, HttpContext http, OctopushyDb db,
            VariableStore store, WebhookSignal signal, WebhookSettingsGate gate, IOptions<OctopushyOptions> options, TimeProvider clock,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            Uri? url = null;
            if (update.Url is not null && Webhook.Rejection(update.Url, options.Value.Notifications.AllowHttpWebhook, out url) is { } rejection)
                return TypedResults.Problem(title: rejection, statusCode: 400);
            if (update.Kinds is { } chosen && (chosen.Count == 0 || chosen.Any(k => !NotificationKinds.All.Contains(k))))
                return TypedResults.Problem(title: $"Choose one or more of {string.Join(", ", NotificationKinds.All)}.", statusCode: 400);

            // One change at a time: two first saves would both add the row, and two URLs would each think their secret was kept.
            return await gate.RunAsync<Results<Ok<WebhookSaved>, ProblemHttpResult>>(() => SaveAsync(update, url, db, store, signal, options.Value, clock, ct), ct);
        })
        .WithName("updateWebhook");

        group.MapDelete("", (OctopushyDb db, VariableStore store, HttpContext http, WebhookSettingsGate gate, TimeProvider clock, CancellationToken ct) =>
            gate.RunAsync(async () =>
            {
                var state = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == 1, ct);
                if (state is not null)
                {
                    // Back to as it was before it was set, the kinds chosen kept.
                    db.Entry(state).CurrentValues.SetValues(new WebhookState
                    {
                        Kinds = state.Kinds, Generation = state.Generation + 1, UpdatedAt = clock.GetUtcNow(),
                    });
                    await db.SaveChangesAsync(ct);
                }

                var actor = http.User.Identity?.Name ?? "operator";
                await store.DeleteInternalAsync(Webhook.UrlKey, actor, ct);
                await store.DeleteInternalAsync(Webhook.SecretKey, actor, ct);
                return TypedResults.NoContent();
            }, ct))
        .WithName("deleteWebhook");

        // Sent at once, past the queue, so the operator sees what the receiver says. Nothing is recorded.
        group.MapPost("/test", async Task<Results<Ok<WebhookTestResult>, ProblemHttpResult>> (VariableStore store, WebhookTransport transport,
            IOptions<OctopushyOptions> options, TimeProvider clock, CancellationToken ct) =>
        {
            if (await Webhook.TargetAsync(store, ct) is not { } target)
                return TypedResults.Problem(title: "Set the webhook's URL first.", statusCode: 400);
            var now = clock.GetUtcNow();
            var id = $"test-{Guid.NewGuid():N}";
            var body = Webhook.Payload(id, Webhook.TestKind, now, "Test event from Octopushy", null, null, null,
                Webhook.Link(options.Value.PublicUrl, null, null, null, null));
            var answer = await transport.PostAsync(target.Url, target.Secret, id, body, now.ToUnixTimeSeconds(), ct);
            return TypedResults.Ok(new WebhookTestResult(answer.Delivered, answer.Status, answer.Result));
        })
        .RequireRateLimiting(TestRateLimit)
        .WithName("testWebhook");
    }

    private static async Task<Results<Ok<WebhookSaved>, ProblemHttpResult>> SaveAsync(WebhookUpdate update, Uri? url, OctopushyDb db,
        VariableStore store, WebhookSignal signal, OctopushyOptions options, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var state = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == 1, ct);
        if (state is null)
        {
            state = new WebhookState { Kinds = [.. Webhook.DefaultKinds] };
            db.Webhooks.Add(state);
        }

        if (url is null && state.Host is null)
            return TypedResults.Problem(title: "Set the webhook's URL first.", statusCode: 400);
        if (update.Kinds is { } kinds)
            state.Kinds = NotificationKinds.All.Where(kinds.Contains).ToArray();

        string? secret = null;
        if (url is not null)
        {
            secret = Webhook.NewSecret();
            await store.AddInternalAsync(Webhook.UrlKey, url.OriginalString.Trim(), Visibilities.Secret, "The webhook notifications are sent to.", ct);
            await store.AddInternalAsync(Webhook.SecretKey, secret, Visibilities.Secret, "The key the webhook's events are signed with.", ct);
            state.Host = Webhook.Clean(url.IdnHost, 255);
            // Sent from now on: what was told before the webhook was set, or while it was stopped, is not sent.
            if (!state.Enabled)
            {
                state.Position = await db.Notifications.MaxAsync(n => (long?)n.Id, ct) ?? 0;
                state.Attempts = 0;
                state.Refusals = 0;
                state.NextAttemptAt = null;
            }

            state.Enabled = true;
            state.StoppedAt = null;
        }

        state.Generation++;
        state.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        signal.Wake();
        return TypedResults.Ok(new WebhookSaved(Dto(state, options), secret));
    }
}
