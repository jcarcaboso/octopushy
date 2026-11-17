using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Store;

namespace Octopushy.Server.Notifications;

/// <summary>Wakes <see cref="WebhookDelivery"/> when a save added a notification, or the webhook's settings changed.</summary>
public sealed class WebhookSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);
}

/// <summary>What one POST to the webhook came to. <see cref="Result"/> is for people: bounded, with no control characters.</summary>
public sealed record WebhookAnswer(bool Delivered, int? Status, string Result)
{
    /// <summary>The receiver said it is gone for good: sending stops until the URL is set again.</summary>
    public bool Gone => Status == (int)HttpStatusCode.Gone;

    /// <summary>The receiver refused this request itself, not for being down: a 4xx other than 408, 410 and 429.</summary>
    public bool Refused => Status is >= 400 and < 500 and not (408 or 410 or 429);
}

/// <summary>
/// The one webhook: where its URL and signing secret are kept, what it sends and how. See docs/notifications.md.
/// <para>
/// The URL and the secret are server-owned secrets in the variable store, under <see cref="UrlKey"/> and <see cref="SecretKey"/>;
/// the operator endpoints for variables refuse every key in <c>system/</c>, and no definition may name one. Neither is ever
/// returned by the API, written to a log, or put in a result: the API shows only the URL's host.
/// </para>
/// </summary>
public static class Webhook
{
    public const string UrlKey = "system/webhook/url";
    public const string SecretKey = "system/webhook/signing-secret";

    public const string TimestampHeader = "X-Octopushy-Timestamp";
    public const string SignatureHeader = "X-Octopushy-Signature";
    public const string EventHeader = "X-Octopushy-Event";

    /// <summary>The kind of the event the test button sends. Never a notification's.</summary>
    public const string TestKind = "test";

    public const int MaxUrlLength = 2000;

    /// <summary>How long one POST may take, connecting included.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>What is sent unless the operator chooses: everything that waits on them or went wrong with an agent.</summary>
    public static readonly IReadOnlyList<string> DefaultKinds =
    [
        NotificationKinds.RunApproval, NotificationKinds.RunFailed, NotificationKinds.RunUncertain,
        NotificationKinds.AgentOffline, NotificationKinds.AgentOnline, NotificationKinds.AgentUpdateFailed,
    ];

    /// <summary>Why <paramref name="text"/> is not a webhook URL, or null with the URL. The text is never quoted back.</summary>
    public static string? Rejection(string? text, bool allowHttp, out Uri? url)
    {
        url = null;
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length is 0 or > MaxUrlLength)
            return $"A webhook URL is 1 to {MaxUrlLength} characters.";
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host)
            || trimmed.Any(char.IsControl) || trimmed.Any(char.IsWhiteSpace))
            return "The webhook URL is not an absolute URL.";
        if (parsed.Scheme != Uri.UriSchemeHttps && !(allowHttp && parsed.Scheme == Uri.UriSchemeHttp))
            return allowHttp ? "A webhook URL starts with https:// or http://." : "A webhook URL starts with https://. Octopushy:Notifications:AllowHttpWebhook allows http:// on a local network.";
        if (!string.IsNullOrEmpty(parsed.UserInfo))
            return "A webhook URL carries no user name or password. Put a token in its path or query instead.";
        if (!string.IsNullOrEmpty(parsed.Fragment))
            return "A webhook URL has no #fragment.";
        url = parsed;
        return null;
    }

    /// <summary>A new signing secret: 32 random bytes, as the receiver is shown them once.</summary>
    public static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// The signature header's value: <c>sha256=</c> and the lowercase hex HMAC-SHA256, keyed with the secret's UTF-8 bytes, of
    /// the timestamp header's value, a dot, and the body's bytes as sent.
    /// </summary>
    public static string Sign(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.ASCII.GetBytes($"{timestamp}.");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
    }

    /// <summary>Text from somewhere else, made safe to show and to send: control characters dropped, at most <paramref name="max"/> characters.</summary>
    public static string Clean(string text, int max)
    {
        var cleaned = new string(text.Where(c => char.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.Control
            or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator or System.Globalization.UnicodeCategory.Surrogate
            or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.OtherNotAssigned)).ToArray()).Trim();
        return cleaned.Length <= max ? cleaned : cleaned[..(max - 1)] + "…";
    }

    /// <summary>The page a notification leads to, under the web app's public URL; null when that is not set.</summary>
    public static string? Link(string? publicUrl, Guid? runId, Guid? resourceId, string? resourceKind, Guid? agentId)
    {
        if (string.IsNullOrWhiteSpace(publicUrl) || !Uri.TryCreate(publicUrl.Trim(), UriKind.Absolute, out var root)
            || root.Scheme is not ("https" or "http"))
            return null;
        var path = runId is { } run ? $"runs/{run}"
            : agentId is not null ? "agents"
            : resourceId is { } id && resourceKind is ResourceKinds.Guest or ResourceKinds.Service or ResourceKinds.Installation ? $"{resourceKind}s/{id}"
            : null;
        return path is null ? root.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" : new Uri(new Uri(root.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"), path).ToString();
    }

    /// <summary>
    /// The body of one event, field by field as docs/notifications.md lists them. It carries ids, the server-written title,
    /// the resource's address and the agent's name, each bounded; never a body, a tool's output or a stored value.
    /// </summary>
    public static byte[] Payload(string id, string kind, DateTimeOffset at, string title, (Guid Id, long Number)? run,
        (Guid Id, string Kind, string Address)? resource, (Guid Id, string Name)? agent, string? link)
    {
        var body = new JsonObject
        {
            ["id"] = id,
            ["kind"] = kind,
            ["test"] = kind == TestKind,
            ["at"] = at.ToUniversalTime().ToString("O"),
            ["title"] = Clean(title, 200),
            ["run"] = run is { } r ? new JsonObject { ["id"] = r.Id.ToString(), ["number"] = r.Number } : null,
            ["resource"] = resource is { } s ? new JsonObject { ["id"] = s.Id.ToString(), ["kind"] = s.Kind, ["address"] = Clean(s.Address, 300) } : null,
            ["agent"] = agent is { } a ? new JsonObject { ["id"] = a.Id.ToString(), ["name"] = Clean(a.Name, 100) } : null,
            ["url"] = link,
        };
        return Encoding.UTF8.GetBytes(body.ToJsonString());
    }

    /// <summary>A notification's event, with what it points at as the database has it now.</summary>
    public static async Task<byte[]> PayloadAsync(OctopushyDb db, Notification notification, string? publicUrl, CancellationToken ct)
    {
        var run = notification.RunId is { } runId
            ? await db.Runs.AsNoTracking().Where(r => r.Id == runId).Select(r => new { r.Id, r.Number }).FirstOrDefaultAsync(ct)
            : null;
        var resource = notification.ResourceId is { } resourceId
            ? await db.Resources.AsNoTracking().Where(r => r.Id == resourceId).Select(r => new { r.Id, r.Kind, r.Address }).FirstOrDefaultAsync(ct)
            : null;
        var agent = notification.AgentId is { } agentId
            ? await db.Agents.AsNoTracking().Where(a => a.Id == agentId).Select(a => new { a.Id, a.Name }).FirstOrDefaultAsync(ct)
            : null;
        return Payload(notification.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), notification.Kind, notification.CreatedAt,
            notification.Title,
            run is null ? null : (run.Id, run.Number),
            resource is null ? null : (resource.Id, resource.Kind, resource.Address),
            agent is null ? (notification.AgentId is { } gone ? (gone, "") : null) : (agent.Id, agent.Name),
            Link(publicUrl, notification.RunId, resource?.Id, resource?.Kind, notification.AgentId));
    }

    /// <summary>The URL and the secret, or null when either is missing.</summary>
    public static async Task<(Uri Url, string Secret)?> TargetAsync(VariableStore store, CancellationToken ct)
    {
        var url = await store.GetAsync(UrlKey, ct);
        var secret = await store.GetAsync(SecretKey, ct);
        return url is { } u && secret is { } s && Uri.TryCreate(u.Value, UriKind.Absolute, out var parsed) ? (parsed, s.Value) : null;
    }
}

/// <summary>
/// Posts events. No redirect is followed, no cookie kept, one POST takes at most <see cref="Webhook.Timeout"/>, and at most a
/// few hundred bytes of the answer are read. It logs nothing: the URL may carry a token. Tests replace the handler.
/// </summary>
public sealed class WebhookTransport(HttpMessageHandler handler) : IDisposable
{
    private readonly HttpMessageInvoker _client = new(handler, disposeHandler: true);

    public WebhookTransport() : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = Webhook.Timeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
    }

    private static readonly string UserAgent = $"Octopushy/{Agents.AgentUpdates.TargetVersion}";

    public async Task<WebhookAnswer> PostAsync(Uri url, string secret, string eventId, byte[] body, long timestamp, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Webhook.Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Add(Webhook.EventHeader, eventId);
        request.Headers.Add(Webhook.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(Webhook.SignatureHeader, Webhook.Sign(secret, timestamp, body));
        try
        {
            using var response = await _client.SendAsync(request, timeout.Token);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return new WebhookAnswer(true, status, $"HTTP {status}");
            var excerpt = await ExcerptAsync(response, timeout.Token);
            var redirect = status is >= 300 and < 400 ? " (redirects are not followed)" : "";
            return new WebhookAnswer(false, status, Webhook.Clean($"HTTP {status}{redirect}{(excerpt.Length > 0 ? ": " + excerpt : "")}", 300));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WebhookAnswer(false, null, $"No answer within {(int)Webhook.Timeout.TotalSeconds} seconds.");
        }
        // The exception's own message may name the address; only its kind is told.
        catch (HttpRequestException e)
        {
            return new WebhookAnswer(false, null, e.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "The host name did not resolve.",
                HttpRequestError.ConnectionError => "The connection failed.",
                HttpRequestError.SecureConnectionError => "The TLS connection failed.",
                HttpRequestError.ProxyTunnelError => "The proxy refused the connection.",
                _ => "The request failed.",
            });
        }
    }

    /// <summary>The start of the answer, as a person may see it.</summary>
    private static async Task<string> ExcerptAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[ReadBytes];
            var read = 0;
            while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), ct) is var n and > 0)
                read += n;
            return Webhook.Clean(Encoding.UTF8.GetString(buffer, 0, read), ExcerptChars);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return "";
        }
    }

    /// <summary>How much of a receiver's answer is read, and how much of it a person is shown.</summary>
    private const int ReadBytes = 512;
    private const int ExcerptChars = 100;

    public void Dispose() => _client.Dispose();
}
