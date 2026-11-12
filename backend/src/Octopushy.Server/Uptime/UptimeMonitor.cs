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
/// <item>Each check keeps a pool: the agents that reached its target, that is connected and spoke the protocol, in the last
/// day. A check goes to the one of them that reached it last, else to the others by name. An agent that cannot connect
/// says something about its own route, not about the target: the check goes on to the next agent at once. Down is
/// recorded when an agent of the pool cannot connect and no other agent can either, or when no agent reached it at all;
/// when only agents outside the pool are connected and none connects, nothing is recorded. An agent that connects but
/// gets a bad answer, such as HTTP 503, joins the pool and its answer stands.</item>
/// <item>Probes are few per session and never fill the queue assignments share. An answer counts only for a probe of
/// the same session still awaited; one that never comes is not evidence of anything, so it records nothing.</item>
/// <item>An agent that says it cannot make the check faithfully, such as through a proxy, records nothing either; like
/// one that does not answer, the next agent is asked.</item>
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

    /// <summary>Announced by agents that also answer <c>tcp</c> probes: only they are asked to check a <c>tcp://</c> link.</summary>
    public const string TcpCapability = "uptime-tcp-v1";

    /// <summary>Probe IDs start with this, so an answer is routed here and never taken as reachability evidence.</summary>
    public const string ProbePrefix = "up-";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan BucketLength = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(31);
    private static readonly TimeSpan TargetsRefresh = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AnswerGrace = TimeSpan.FromSeconds(30);
    /// <summary>How long an agent stays in a check's pool after it last reached the target.</summary>
    public static readonly TimeSpan PoolLifetime = TimeSpan.FromDays(1);
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

        /// <summary>The pool: agents that reached the target, by agent ID, and when they last did. In memory only.</summary>
        public Dictionary<Guid, DateTimeOffset> Reached { get; } = [];

        /// <summary>When the current round sent its first probe; null between rounds.</summary>
        public DateTimeOffset? RoundStart { get; set; }

        /// <summary>The agents this round asked.</summary>
        public HashSet<Guid> Asked { get; } = [];

        /// <summary>The down answer this round would record: the first from the pool, else the first.</summary>
        public Down? Down { get; set; }

        /// <summary>Whether an agent of the pool did not answer this round, or could not check: it might have reached it.</summary>
        public bool PoolUnsure { get; set; }

        public void EndRound()
        {
            NextAt = (RoundStart ?? NextAt) + Interval;
            RoundStart = null;
            Asked.Clear();
            Down = null;
            PoolUnsure = false;
        }
    }

    private sealed record Pending(string Key, Guid Session, Guid Agent, DateTimeOffset Until);

    private sealed record Down(string Detail, string Agent, bool FromPool, DateTimeOffset At);

    private sealed record Verdict(string Key, bool Up, string Detail, int? ElapsedMs, string Agent, DateTimeOffset At);

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
        var verdicts = new List<Verdict>();
        lock (_gate)
        {
            foreach (var (id, pending) in _outstanding.ToList())
            {
                if (pending.Until > now)
                    continue;
                _outstanding.Remove(id);
                // No answer says nothing about the target: the round goes on to the next agent.
                if (_slots.TryGetValue(pending.Key, out var slot) && slot.OutstandingId == id)
                {
                    (slot.OutstandingId, slot.NextAt) = (null, now);
                    slot.PoolUnsure |= slot.Reached.ContainsKey(pending.Agent);
                }
            }

            if (sessions.Count == 0)
                return;
            var busy = _outstanding.Values.GroupBy(p => p.Session).ToDictionary(g => g.Key, g => g.Count());
            bool Free(AgentSession s) => busy.GetValueOrDefault(s.Id) < MaxOutstanding && (s.Responder is not null || s.Queued < MaxQueued);
            foreach (var slot in _slots.Values.Where(s => s.OutstandingId is null && s.NextAt <= now).OrderBy(s => s.NextAt).ToList())
            {
                foreach (var gone in slot.Reached.Where(r => now - r.Value > PoolLifetime).Select(r => r.Key).ToList())
                    slot.Reached.Remove(gone);
                var candidates = Candidates(slot, sessions);
                if (candidates.Count == 0)
                {
                    // Every connected agent was asked this round, and none reached the target.
                    if (Conclude(slot) is { } verdict)
                        verdicts.Add(verdict);
                    continue;
                }

                if (candidates.FirstOrDefault(Free) is not { } session)
                    continue;
                var probe = ProbeFor(slot.Target);
                slot.OutstandingId = probe.ProbeId;
                slot.RoundStart ??= now;
                slot.Asked.Add(session.AgentId);
                slot.NextAt = slot.RoundStart.Value + Interval;
                _outstanding[probe.ProbeId] = new(slot.Target.Key, session.Id, session.AgentId, now + TimeSpan.FromSeconds(TimeoutSeconds) + AnswerGrace);
                busy[session.Id] = busy.GetValueOrDefault(session.Id) + 1;
                sends.Add((session, probe));
            }
        }

        foreach (var verdict in verdicts)
            await StoreAsync(verdict, cancellationToken);

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
                    {
                        slot.Asked.Remove(pending.Agent);
                        (slot.OutstandingId, slot.NextAt) = (null, now);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The connected agents this round has not asked yet: the pool first, the one that reached the target last first, then
    /// the others by name.
    /// </summary>
    private static List<AgentSession> Candidates(Slot slot, IEnumerable<AgentSession> sessions) =>
        sessions.Where(s => !slot.Asked.Contains(s.AgentId) && (slot.Target.Protocol != "tcp" || s.Capabilities.Contains(TcpCapability)))
            .OrderByDescending(s => slot.Reached.TryGetValue(s.AgentId, out var at) ? at : DateTimeOffset.MinValue)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Ends a round in which no agent reached the target: down when an agent of the pool could not connect and none of the
    /// pool left it unsure, or when there is no pool; else nothing, since the agents that answered may simply have no route
    /// there.
    /// </summary>
    private static Verdict? Conclude(Slot slot)
    {
        var down = slot.Down;
        var key = slot.Target.Key;
        var record = down is not null && (down.FromPool && !slot.PoolUnsure || slot.Reached.Count == 0);
        slot.EndRound();
        return record ? new Verdict(key, false, down!.Detail, null, down.Agent, down.At) : null;
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
        Verdict? verdict = null;
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
            var judged = Judge(slot.Target, report);
            if (Reached(report))
            {
                // It connected and spoke the protocol: it is in the pool, and its answer stands.
                slot.Reached[session.AgentId] = received;
                if (judged is { } j)
                    verdict = new(slot.Target.Key, j.Up, j.Detail, j.Up ? (int)Math.Min(report.ElapsedMs, int.MaxValue) : null, session.Name, received);
                slot.EndRound();
            }
            else
            {
                // It could not connect, or could not check: that may be its own route. The next agent is asked at once.
                slot.PoolUnsure |= judged is null && slot.Reached.ContainsKey(session.AgentId);
                if (judged is { } j && (slot.Down is null || (!slot.Down.FromPool && slot.Reached.ContainsKey(session.AgentId))))
                    slot.Down = new(j.Detail, session.Name, slot.Reached.ContainsKey(session.AgentId), received);
                slot.NextAt = received;
                var connected = connections.All.Where(s => s.Capabilities.Contains(Capability));
                if (Candidates(slot, connected).Count == 0)
                    verdict = Conclude(slot);
            }
        }

        if (verdict is not null)
            await StoreAsync(verdict, cancellationToken);
        return true;
    }

    /// <summary>Whether the agent connected to the target and spoke its protocol, whatever the target answered.</summary>
    public static bool Reached(ReachabilityReport report) => report.Status == "reachable";

    private async Task StoreAsync(Verdict verdict, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await RecordAsync(scope.ServiceProvider.GetRequiredService<OctopushyDb>(), verdict.Key, verdict.Up, verdict.Detail, verdict.ElapsedMs,
                verdict.Agent, verdict.At, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Recording an uptime answer for {Key} failed.", verdict.Key);
        }
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
                return (true, target.Protocol switch { "ssh" => "SSH answered", "tcp" => "TCP connection opened", _ => "TLS handshake completed" });
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
