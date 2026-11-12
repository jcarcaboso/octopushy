using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Git;

namespace Octopushy.Server.Uptime;

/// <summary>
/// Checks every <see cref="UptimeTarget"/> about once a minute through a connected agent that answers uptime probes, and
/// records what it answers. The server never connects anywhere itself: agents do the network work, as they do for steps.
/// <list type="bullet">
/// <item>A check goes to the agent that answered it last, while it is connected; else to the first by name. One agent
/// that reaches everything is enough. What another agent would see is not asked.</item>
/// <item>Probes are few per session and never fill the queue assignments share. An answer counts only for a probe of
/// the same session still awaited; one that never comes is not evidence of anything, so it records nothing.</item>
/// <item>An agent that says it cannot make the check faithfully, such as through a proxy, records nothing either. Every
/// other answer is about the target: up, or down with why.</item>
/// </list>
/// See docs/uptime.md.
/// </summary>
public sealed class UptimeMonitor(
    AgentConnections connections,
    IServiceScopeFactory scopes,
    RevisionCache revisions,
    TimeProvider clock,
    ILogger<UptimeMonitor> logger)
{
    public const string Capability = "uptime-v1";

    /// <summary>Probe IDs start with this, so an answer is routed here and never taken as reachability evidence.</summary>
    public const string ProbePrefix = "up-";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan BucketLength = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(31);
    private static readonly TimeSpan TargetsRefresh = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AnswerGrace = TimeSpan.FromSeconds(30);
    public const int TimeoutSeconds = 10;
    /// <summary>Uptime probes awaited per session; the agent runs a few at a time and refuses more than 32 queued.</summary>
    public const int MaxOutstanding = 8;
    /// <summary>A probe goes out only while fewer messages wait than this, as for reachability probes.</summary>
    public const int MaxQueued = 32;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> _outstanding = new(StringComparer.Ordinal);
    private IReadOnlyList<UptimeTarget> _targets = [];
    private DateTimeOffset _targetsAt = DateTimeOffset.MinValue;
    private DateTimeOffset _prunedAt = DateTimeOffset.MinValue;

    private sealed class Slot(UptimeTarget target, DateTimeOffset next)
    {
        public UptimeTarget Target { get; set; } = target;
        public DateTimeOffset NextAt { get; set; } = next;
        public string? OutstandingId { get; set; }
        public Guid? LastSession { get; set; }
    }

    private sealed record Pending(string Key, Guid Session, DateTimeOffset Until);

    /// <summary>The checks as of the last refresh, for the API.</summary>
    public IReadOnlyList<UptimeTarget> Targets
    {
        get
        {
            lock (_gate)
                return _targets;
        }
    }

    /// <summary>Whether a connected agent answers uptime probes.</summary>
    public bool Checking => connections.All.Any(s => s.Capabilities.Contains(Capability));

    /// <summary>One pass: refreshes the checks when due, gives up on overdue answers, and sends what is due.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (now - _targetsAt >= TargetsRefresh)
            await RefreshTargetsAsync(now, cancellationToken);
        if (now - _prunedAt >= TimeSpan.FromHours(1))
            await PruneAsync(now, cancellationToken);

        var sessions = connections.All.Where(s => s.Capabilities.Contains(Capability)).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        var sends = new List<(AgentSession Session, ReachabilityProbe Probe)>();
        lock (_gate)
        {
            foreach (var (id, pending) in _outstanding.ToList())
            {
                if (pending.Until > now)
                    continue;
                _outstanding.Remove(id);
                if (_slots.TryGetValue(pending.Key, out var slot) && slot.OutstandingId == id)
                    slot.OutstandingId = null;
            }

            if (sessions.Count == 0)
                return;
            var busy = _outstanding.Values.GroupBy(p => p.Session).ToDictionary(g => g.Key, g => g.Count());
            foreach (var slot in _slots.Values.Where(s => s.OutstandingId is null && s.NextAt <= now).OrderBy(s => s.NextAt))
            {
                bool Free(AgentSession s) => busy.GetValueOrDefault(s.Id) < MaxOutstanding && (s.Responder is not null || s.Queued < MaxQueued);
                var session = sessions.FirstOrDefault(s => s.Id == slot.LastSession && Free(s)) ?? sessions.FirstOrDefault(Free);
                if (session is null)
                    break;
                var probe = ProbeFor(slot.Target);
                slot.OutstandingId = probe.ProbeId;
                slot.LastSession = session.Id;
                slot.NextAt = now + Interval;
                _outstanding[probe.ProbeId] = new(slot.Target.Key, session.Id, now + TimeSpan.FromSeconds(TimeoutSeconds) + AnswerGrace);
                busy[session.Id] = busy.GetValueOrDefault(session.Id) + 1;
                sends.Add((session, probe));
            }
        }

        foreach (var (session, probe) in sends)
        {
            if (session.Responder is { } respond)
            {
                await AcceptAsync(session, respond(probe), cancellationToken);
                continue;
            }

            if (!session.TrySend(new ServerMessage { Probe = probe }))
            {
                lock (_gate)
                {
                    if (_outstanding.Remove(probe.ProbeId, out var pending) && _slots.TryGetValue(pending.Key, out var slot))
                        (slot.OutstandingId, slot.NextAt) = (null, now);
                }
            }
        }
    }

    private static ReachabilityProbe ProbeFor(UptimeTarget target) => new()
    {
        ProbeId = ProbePrefix + Guid.NewGuid().ToString("N"),
        Protocol = target.Protocol,
        Host = target.HostName,
        Port = (uint)target.Port,
        ServerName = target.ServerName ?? "",
        // A Proxmox API check is liveness, not identity: any certificate. A service link is checked as a browser would.
        TlsTrust = target.Kind == UptimeTarget.ProxmoxApi ? "collect" : target.Tls ? "system" : "",
        Context = "agent",
        TimeoutSeconds = TimeoutSeconds,
        Path = target.Path ?? "",
        Tls = target.Tls,
    };

    private async Task RefreshTargetsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IReadOnlyList<UptimeTarget> targets;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            targets = await UptimeTarget.ForAsync(scope.ServiceProvider.GetRequiredService<OctopushyDb>(), revisions, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Reading what to check for uptime failed; keeping the previous checks.");
            _targetsAt = now;
            return;
        }

        lock (_gate)
        {
            _targets = targets;
            _targetsAt = now;
            var keys = targets.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in _slots.Keys.Where(k => !keys.Contains(k)).ToList())
                _slots.Remove(gone);
            // New checks spread over the interval, so a large repository does not probe all at once.
            var spread = targets.Count == 0 ? TimeSpan.Zero : Interval / targets.Count;
            var i = 0;
            foreach (var target in targets)
            {
                if (_slots.TryGetValue(target.Key, out var slot))
                    slot.Target = target;
                else
                    _slots[target.Key] = new Slot(target, now + spread * i);
                i++;
            }
        }
    }

    /// <summary>
    /// Records an answer to one of this monitor's probes, if the same session still awaits it. Returns whether the probe
    /// was this monitor's, taken or not, so that it is never read as reachability evidence.
    /// </summary>
    public async Task<bool> AcceptAsync(AgentSession session, ReachabilityReport report, CancellationToken cancellationToken)
    {
        if (!report.ProbeId.StartsWith(ProbePrefix, StringComparison.Ordinal))
            return false;
        var received = clock.GetUtcNow();
        UptimeTarget target;
        lock (_gate)
        {
            if (!_outstanding.TryGetValue(report.ProbeId, out var pending) || pending.Session != session.Id || pending.Until <= received
                || !_slots.TryGetValue(pending.Key, out var slot) || slot.OutstandingId != report.ProbeId)
            {
                logger.LogDebug("Agent {Agent} answered an uptime probe nothing waits for; dropped.", session.AgentId);
                return true;
            }

            _outstanding.Remove(report.ProbeId);
            slot.OutstandingId = null;
            target = slot.Target;
        }

        if (Judge(target, report) is not { } verdict)
            return true;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await RecordAsync(scope.ServiceProvider.GetRequiredService<OctopushyDb>(), target.Key, verdict.Up, verdict.Detail,
                verdict.Up ? (int)Math.Min(report.ElapsedMs, int.MaxValue) : null, session.Name, received, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Recording an uptime answer for {Key} failed.", target.Key);
        }

        return true;
    }

    /// <summary>
    /// What an answer says about its target: up, or down with why. Null when it says nothing about the target, because the
    /// agent could not make the check as asked.
    /// </summary>
    public static (bool Up, string Detail)? Judge(UptimeTarget target, ReachabilityReport report)
    {
        var detail = Truncate(report.Detail, 300);
        switch (report.Status)
        {
            case "unsupported":
                return null;
            case "reachable" when target.Protocol == "http":
                var code = (int)report.HttpStatus;
                return code is >= 200 and < 400 or 401 or 403
                    ? (true, $"HTTP {code}")
                    : (false, code == 0 ? "no HTTP status was read" : $"HTTP {code}");
            case "reachable":
                return (true, target.Protocol == "ssh" ? "SSH answered" : "TLS handshake completed");
            default:
                return (false, detail.Length > 0 ? detail : report.Status);
        }
    }

    /// <summary>Stores one answer: the check's current state, and a count in its 15-minute bucket.</summary>
    public static async Task RecordAsync(OctopushyDb db, string key, bool up, string? detail, int? elapsedMs, string? agent, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var check = await db.UptimeChecks.FirstOrDefaultAsync(c => c.Key == key, cancellationToken);
        if (check is null)
        {
            check = new UptimeCheck { Key = key, Up = up, Since = at };
            db.UptimeChecks.Add(check);
        }
        else if (check.Up != up)
        {
            check.Since = at;
        }

        check.Up = up;
        check.CheckedAt = at;
        check.ElapsedMs = elapsedMs;
        check.Detail = detail;
        check.Agent = agent;
        await db.SaveChangesAsync(cancellationToken);

        var bucket = BucketOf(at);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO uptime_buckets (check_key, starts_at, up, down, latency_ms_total)
            VALUES ({key}, {bucket}, {(up ? 1 : 0)}, {(up ? 0 : 1)}, {(long)(up ? elapsedMs ?? 0 : 0)})
            ON CONFLICT (check_key, starts_at) DO UPDATE SET
                up = uptime_buckets.up + excluded.up,
                down = uptime_buckets.down + excluded.down,
                latency_ms_total = uptime_buckets.latency_ms_total + excluded.latency_ms_total
            """, cancellationToken);
    }

    /// <summary>The start of the 15-minute bucket <paramref name="at"/> falls in, in UTC.</summary>
    public static DateTimeOffset BucketOf(DateTimeOffset at)
    {
        var ticks = at.UtcTicks - at.UtcTicks % BucketLength.Ticks;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private async Task PruneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        _prunedAt = now;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
            var before = now - Retention;
            await db.UptimeBuckets.Where(b => b.StartsAt < before).ExecuteDeleteAsync(cancellationToken);
            await db.UptimeChecks.Where(c => c.CheckedAt < before).ExecuteDeleteAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Removing old uptime records failed; it will try again.");
        }
    }

    private static string Truncate(string text, int max)
    {
        var clean = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return clean.Length <= max ? clean : clean[..(max - 1)] + "…";
    }
}

/// <summary>Runs <see cref="UptimeMonitor"/> every few seconds; each check itself is due about once a minute.</summary>
public sealed class UptimeRunner(UptimeMonitor monitor, ILogger<UptimeRunner> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await monitor.TickAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Checking uptime failed; it will try again.");
            }

            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
