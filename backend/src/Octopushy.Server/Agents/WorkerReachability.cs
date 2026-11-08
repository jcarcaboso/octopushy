using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Octopushy.Agent.V1;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Server.Agents;

/// <summary>
/// An endpoint a step connects to, and from where: the agent process (the <c>proxmox</c> executor) or the tool runtime
/// (OpenTofu, Ansible). Derived from trusted definitions and queued work only; see <see cref="NetworkRequirements"/>.
/// </summary>
/// <param name="Kind"><c>proxmox-api</c>, <c>snippets-ssh</c> or <c>guest-ssh</c>: what it is, for people.</param>
/// <param name="Protocol"><c>tls</c> or <c>ssh</c>.</param>
/// <param name="ServerName">TLS only: the name presented and verified.</param>
/// <param name="Trust">TLS only: <c>system</c>, <c>ca</c> or <c>insecure</c>, as the connection is configured; <c>none</c> for SSH.</param>
/// <param name="Image">The tool's image: where a container makes the connection, it is part of where from.</param>
/// <param name="Unsupported">Why this connection cannot be probed faithfully, known without asking any agent.</param>
public sealed record NetworkTarget(
    string Kind,
    string Protocol,
    string Host,
    int Port,
    string? ServerName,
    string Trust,
    string? CaKey,
    int? CaVersion,
    string Context,
    string? Image,
    string? Unsupported = null)
{
    public const string AgentContext = "agent";
    public const string ToolContext = "tool";

    public string Endpoint => $"{(Protocol == "tls" ? "https" : "ssh")}://{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}";

    /// <summary>Whether a container of <see cref="Image"/> makes the connection on an agent with this runtime.</summary>
    public static bool InContainer(string context, string? runtime) => context == ToolContext && runtime is "podman" or "docker";

    /// <summary>
    /// What evidence is about on an agent with <paramref name="runtime"/>: everything that decides where a connection goes
    /// and how it is judged. A change to any of it is a new key, so older evidence never answers for it.
    /// </summary>
    public string KeyFor(string? runtime) => string.Join('|', Protocol, Host.ToLowerInvariant(), Port, ServerName?.ToLowerInvariant(), Trust,
        CaKey, CaVersion, Context, InContainer(Context, runtime) ? Image : null, Unsupported is null ? null : "unsupported");

    public string Label => $"{Kind switch
    {
        "proxmox-api" => "Proxmox API",
        "snippets-ssh" => "snippet upload SSH",
        "guest-ssh" => "guest SSH",
        _ => Kind,
    }} {Endpoint} from the {(Context == AgentContext ? "agent process" : "tool runtime")}";

    /// <summary>Whether two targets are the same endpoint, judged the same way, wherever they are reached from.</summary>
    public bool SameEndpoint(NetworkTarget other) => Protocol == other.Protocol && Port == other.Port && Trust == other.Trust
        && string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ServerName, other.ServerName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What one session's agent was found to reach. Lives and dies with the session.</summary>
public sealed class SessionReachability
{
    internal readonly Lock Gate = new();
    internal readonly Dictionary<string, Evidence> Entries = new(StringComparer.Ordinal);
    /// <summary>Probe IDs sent and not yet answered, to their evidence keys.</summary>
    internal readonly Dictionary<string, string> Outstanding = new(StringComparer.Ordinal);

    internal sealed class Evidence(NetworkTarget target, DateTimeOffset now)
    {
        public NetworkTarget Target { get; } = target;
        /// <summary><c>pending</c> until the first answer, then the last one: an agent's status, or <c>no-answer</c>.</summary>
        public string State { get; set; } = WorkerReachability.Pending;
        public string? Detail { get; set; }
        /// <summary>When the server received the last answer. Agent clocks are never used.</summary>
        public DateTimeOffset? CheckedAt { get; set; }
        public DateTimeOffset NextProbeAt { get; set; } = now;
        public int Failures { get; set; }
        public string? LeafSha256 { get; set; }
        public string? OutstandingProbeId { get; set; }
        public DateTimeOffset? OutstandingUntil { get; set; }
        public DateTimeOffset LastWanted { get; set; } = now;
    }
}

/// <summary>Whether a session may get work that needs some connections, and if not, why, for people.</summary>
public sealed record ReachabilityVerdict(bool Ready, string? Reason);

/// <summary>One entry of a session's evidence, as the API shows it.</summary>
public sealed record ReachabilityEntry(
    string Kind,
    string Endpoint,
    string Context,
    string? Image,
    string State,
    string? Detail,
    DateTimeOffset? CheckedAt,
    DateTimeOffset? ExpiresAt,
    bool Usable,
    bool Probing,
    string? LeafSha256);

/// <summary>
/// Which connected agent can reach what, from probes the controller sends and the agents answer. The controller stays
/// the only scheduler: evidence decides which agents a step's attempt may go to, never that one takes it.
/// <list type="bullet">
/// <item>Evidence belongs to a session (<see cref="AgentSession.Reachability"/>), so a reconnect forgets it, and to
/// everything that decides where a connection goes (<see cref="NetworkTarget.KeyFor"/>).</item>
/// <item>A probe goes out without waiting: dispatch asks, sets the attempt's reason and moves on, and an answer wakes it.
/// Probes are few per session and never fill the queue assignments share.</item>
/// <item>An answer counts only for an outstanding probe of the same session, and is timed by this server.</item>
/// <item>Positive evidence is route and protocol: no credential was used, so it proves no login, privilege or outcome.</item>
/// </list>
/// See docs/worker-routing.md.
/// </summary>
public sealed class WorkerReachability(
    AgentConnections connections,
    OrchestratorSignal signal,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<WorkerReachability> logger)
{
    public const string Capability = "reachability-v1";
    public const string Pending = "pending";
    public const string Reachable = "reachable";
    public const string NoAnswer = "no-answer";
    public const string UpgradeNeeded = "update the agent to report reachability";

    /// <summary>When positive evidence is probed again.</summary>
    public static readonly TimeSpan Refresh = TimeSpan.FromMinutes(5);
    /// <summary>How long positive evidence counts without a newer answer.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    /// <summary>Beyond a probe's own timeout, how long its answer may take to arrive.</summary>
    private static readonly TimeSpan AnswerGrace = TimeSpan.FromSeconds(30);
    /// <summary>A forgotten target is dropped after this long, unless an answer for it is awaited.</summary>
    private static readonly TimeSpan Forget = TimeSpan.FromHours(1);

    public const int MaxOutstanding = 8;
    /// <summary>A probe goes out only while fewer messages wait than this, far below <see cref="AgentSession.OutboundCapacity"/>.</summary>
    public const int MaxQueued = 32;
    private const int MaxEntries = 256;
    public const int AgentTimeoutSeconds = 15;
    /// <summary>A container probe may pull the step's image first.</summary>
    public const int ContainerTimeoutSeconds = 120;
    public const int MaxDetail = 300;
    public const int MaxCertificates = 8;
    public const int MaxCertificateBytes = 16 * 1024;

    private static readonly HashSet<string> Statuses =
        [Reachable, "refused", "timeout", "dns", "unreachable", "protocol", "tls-untrusted", "unsupported", "error"];

    /// <summary>
    /// Whether <paramref name="session"/> may get work that connects to every one of <paramref name="targets"/>, asking
    /// its agent about what it has no current evidence for. Never waits for an answer.
    /// </summary>
    public async Task<ReachabilityVerdict> CheckAsync(AgentSession session, IReadOnlyList<NetworkTarget> targets, CancellationToken cancellationToken)
    {
        if (!session.ReportsReachability)
            return new(false, UpgradeNeeded);
        var reasons = new List<string>();
        foreach (var target in targets)
        {
            if (await CheckOneAsync(session, target, cancellationToken) is { } reason)
                reasons.Add(reason);
        }

        if (reasons.Count == 0 && Ambiguity(session, targets) is { } ambiguous)
            reasons.Add(ambiguous);
        return new(reasons.Count == 0, reasons.Count == 0 ? null : string.Join(", ", reasons));
    }

    private async Task<string?> CheckOneAsync(AgentSession session, NetworkTarget target, CancellationToken cancellationToken)
    {
        if (target.Unsupported is { } unsupported)
            return $"{target.Label}: unsupported: {unsupported}";
        var state = session.Reachability;
        var key = target.KeyFor(session.ToolRuntime);
        var now = clock.GetUtcNow();
        SessionReachability.Evidence evidence;
        bool due;
        lock (state.Gate)
        {
            evidence = Entry(state, key, target, now);
            evidence.LastWanted = now;
            ExpireOutstanding(state, now);
            due = evidence.OutstandingProbeId is null && now >= evidence.NextProbeAt;
        }

        if (due)
            await RequestAsync(session, evidence, key, cancellationToken);
        lock (state.Gate)
        {
            now = clock.GetUtcNow();
            return Usable(evidence, now) ? null : $"{target.Label}: {Describe(evidence, now)}";
        }
    }

    private static SessionReachability.Evidence Entry(SessionReachability state, string key, NetworkTarget target, DateTimeOffset now)
    {
        if (state.Entries.TryGetValue(key, out var existing))
            return existing;
        if (state.Entries.Count >= MaxEntries)
        {
            foreach (var (old, _) in state.Entries.Where(e => e.Value.OutstandingProbeId is null)
                         .OrderBy(e => e.Value.LastWanted).Take(state.Entries.Count - MaxEntries + 1).ToList())
                state.Entries.Remove(old);
        }

        var created = new SessionReachability.Evidence(target, now);
        state.Entries[key] = created;
        return created;
    }

    private static bool Usable(SessionReachability.Evidence evidence, DateTimeOffset now) =>
        evidence.State == Reachable && evidence.CheckedAt is { } at && at + Expiry > now;

    private static string Describe(SessionReachability.Evidence e, DateTimeOffset now)
    {
        var probing = e.OutstandingProbeId is not null;
        if (e.CheckedAt is not { } at)
            return probing ? "probing" : "waiting to probe";
        var age = Age(now - at);
        var what = e.State == Reachable
            ? $"reachable {age} ago, too long to count"
            : $"{e.State} {age} ago{(e.Detail is { Length: > 0 } d ? $" ({Truncate(d, 120)})" : "")}";
        return probing ? $"{what}; probing again"
            : e.NextProbeAt > now ? $"{what}; next probe in {Age(e.NextProbeAt - now)}"
            : $"{what}; waiting to probe";
    }

    private static string Age(TimeSpan span) => span.TotalSeconds < 90 ? $"{Math.Max(0, (int)span.TotalSeconds)}s"
        : span.TotalMinutes < 90 ? $"{(int)span.TotalMinutes}m" : $"{(int)span.TotalHours}h";

    /// <summary>Gives up on every probe of the session whose answer is overdue, so that it no longer holds a slot.</summary>
    private static void ExpireOutstanding(SessionReachability state, DateTimeOffset now)
    {
        foreach (var (id, key) in state.Outstanding.ToList())
        {
            if (!state.Entries.TryGetValue(key, out var evidence) || evidence.OutstandingProbeId != id)
            {
                state.Outstanding.Remove(id);
                continue;
            }

            if (evidence.OutstandingUntil > now)
                continue;
            state.Outstanding.Remove(id);
            evidence.OutstandingProbeId = null;
            evidence.OutstandingUntil = null;
            Record(evidence, NoAnswer, "the agent did not answer the probe in time", null, now);
        }
    }

    private static void Record(SessionReachability.Evidence evidence, string status, string? detail, string? leaf, DateTimeOffset now)
    {
        evidence.State = status;
        evidence.Detail = detail;
        evidence.CheckedAt = now;
        evidence.LeafSha256 = leaf;
        if (status == Reachable)
        {
            evidence.Failures = 0;
            evidence.NextProbeAt = now + Refresh;
        }
        else
        {
            evidence.Failures++;
            var backoff = FirstBackoff * Math.Pow(2, Math.Min(evidence.Failures - 1, 10));
            evidence.NextProbeAt = now + (backoff < MaxBackoff ? backoff : MaxBackoff);
        }
    }

    /// <summary>Sends a probe, unless the session has enough in flight or its queue is busy: then the next check asks again.</summary>
    private async Task RequestAsync(AgentSession session, SessionReachability.Evidence evidence, string key, CancellationToken cancellationToken)
    {
        var state = session.Reachability;
        var target = evidence.Target;
        var container = NetworkTarget.InContainer(target.Context, session.ToolRuntime);
        ReachabilityProbe probe;
        lock (state.Gate)
        {
            if (evidence.OutstandingProbeId is not null || state.Outstanding.Count >= MaxOutstanding
                || session.Responder is null && session.Queued >= MaxQueued)
                return;
            probe = new ReachabilityProbe
            {
                ProbeId = Guid.NewGuid().ToString("N"),
                Protocol = target.Protocol,
                Host = target.Host,
                Port = (uint)target.Port,
                ServerName = target.ServerName ?? "",
                TlsTrust = target.Trust == "system" ? "system" : target.Protocol == "tls" ? "collect" : "",
                Context = target.Context,
                Image = target.Context == NetworkTarget.ToolContext ? target.Image ?? "" : "",
                TimeoutSeconds = (uint)(container ? ContainerTimeoutSeconds : AgentTimeoutSeconds),
            };
            evidence.OutstandingProbeId = probe.ProbeId;
            evidence.OutstandingUntil = clock.GetUtcNow() + TimeSpan.FromSeconds(probe.TimeoutSeconds) + AnswerGrace;
            state.Outstanding[probe.ProbeId] = key;
        }

        if (session.Responder is { } respond)
        {
            await AcceptAsync(session, respond(probe), cancellationToken);
            return;
        }

        if (!session.TrySend(new ServerMessage { Probe = probe }))
        {
            lock (state.Gate)
            {
                state.Outstanding.Remove(probe.ProbeId);
                if (evidence.OutstandingProbeId == probe.ProbeId)
                    (evidence.OutstandingProbeId, evidence.OutstandingUntil) = (null, null);
            }
        }
    }

    /// <summary>
    /// Records an agent's answer, if it answers a probe of this session that is still outstanding; anything else is
    /// dropped. Returns whether it was taken.
    /// </summary>
    public async Task<bool> AcceptAsync(AgentSession session, ReachabilityReport report, CancellationToken cancellationToken)
    {
        var received = clock.GetUtcNow();
        var state = session.Reachability;
        SessionReachability.Evidence evidence;
        lock (state.Gate)
        {
            // An answer after its deadline is not taken: the probe was given up on, as if never answered.
            ExpireOutstanding(state, received);
            if (!state.Outstanding.TryGetValue(report.ProbeId, out var key) || !state.Entries.TryGetValue(key, out evidence!)
                || evidence.OutstandingProbeId != report.ProbeId)
            {
                logger.LogDebug("Agent {Agent} answered a probe this session is not waiting for; dropped.", session.AgentId);
                return false;
            }
        }

        var (status, detail, leaf) = await JudgeAsync(evidence.Target, report, received, cancellationToken);
        lock (state.Gate)
        {
            // Answered twice, or given up on meanwhile: the first decision stands.
            if (evidence.OutstandingProbeId != report.ProbeId || !state.Outstanding.Remove(report.ProbeId))
                return false;
            evidence.OutstandingProbeId = null;
            evidence.OutstandingUntil = null;
            Record(evidence, status, detail, leaf, received);
        }

        signal.Wake();
        return true;
    }

    /// <summary>What an answer means for its target. A TLS chain is judged here, never by an agent given the CA.</summary>
    private async Task<(string Status, string? Detail, string? Leaf)> JudgeAsync(NetworkTarget target, ReachabilityReport report, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (report.Detail.Length > MaxDetail * 4 || report.Certificates.Count > MaxCertificates
            || report.Certificates.Any(c => c.Length > MaxCertificateBytes))
            return ("error", "the agent's report was larger than allowed", null);
        var status = Statuses.Contains(report.Status) ? report.Status : "error";
        var detail = Truncate(report.Detail, MaxDetail);
        if (status != Reachable || target.Protocol != "tls")
            return (status, detail, null);
        switch (target.Trust)
        {
            case "system":
                return (status, detail, null);
            case "insecure":
                return report.Certificates.Count == 0
                    ? ("error", "the agent reported no certificate", null)
                    : (status, "TLS handshake completed; the certificate is not verified (verifyTls is off)",
                        Convert.ToHexStringLower(SHA256.HashData(report.Certificates[0].Span)));
            case "ca":
                return await ChainAsync(target, report, now, cancellationToken);
            default:
                return ("error", $"unknown trust {target.Trust}", null);
        }
    }

    /// <summary>
    /// The presented chain against the configured CA alone and the name the connection verifies, as the agent's client
    /// would: no revocation check, no download of missing certificates, nothing fetched at all.
    /// </summary>
    private async Task<(string, string?, string?)> ChainAsync(NetworkTarget target, ReachabilityReport report, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (report.Certificates.Count == 0)
            return ("error", "the agent reported no certificate", null);
        if (target.CaKey is not { } caKey || target.CaVersion is not { } version)
            return ("error", "the CA to judge the certificate by is not known", null);
        string? pem;
        await using (var scope = scopes.CreateAsyncScope())
            pem = await scope.ServiceProvider.GetRequiredService<VariableStore>().GetVersionInternalAsync(caKey, version, cancellationToken);
        if (pem is null)
            return ("error", $"the CA {caKey} v{version} is not in the store", null);
        return JudgeChain(pem, report.Certificates.Select(c => c.ToByteArray()).ToList(), target.ServerName ?? target.Host, now);
    }

    /// <summary>Whether <paramref name="chain"/> (leaf first) is trusted by <paramref name="caPem"/> alone for <paramref name="name"/>.</summary>
    internal static (string Status, string? Detail, string? Leaf) JudgeChain(string caPem, IReadOnlyList<byte[]> chain, string name, DateTimeOffset now)
    {
        try
        {
            var roots = new X509Certificate2Collection();
            roots.ImportFromPem(caPem);
            if (roots.Count == 0)
                return ("error", "the configured CA holds no certificate", null);
            using var leaf = X509CertificateLoader.LoadCertificate(chain[0]);
            using var policy = new X509Chain();
            policy.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            policy.ChainPolicy.CustomTrustStore.AddRange(roots);
            foreach (var intermediate in chain.Skip(1))
                policy.ChainPolicy.ExtraStore.Add(X509CertificateLoader.LoadCertificate(intermediate));
            policy.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            policy.ChainPolicy.DisableCertificateDownloads = true;
            policy.ChainPolicy.VerificationTime = now.UtcDateTime;
            policy.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(chain[0]));
            if (!policy.Build(leaf))
            {
                var why = string.Join("; ", policy.ChainStatus.Select(s => s.Status.ToString()).Distinct());
                return ("tls-untrusted", $"the certificate does not chain to the configured CA ({why})", fingerprint);
            }

            return leaf.MatchesHostname(name, allowWildcards: true, allowCommonName: false)
                ? (Reachable, "TLS handshake completed; the certificate chains to the configured CA and names the host", fingerprint)
                : ("tls-untrusted", $"the certificate does not name {name}", fingerprint);
        }
        catch (CryptographicException e)
        {
            return ("tls-untrusted", $"the presented certificate cannot be read: {Truncate(e.Message, 120)}", null);
        }
    }

    /// <summary>
    /// With verification off, which machine answers is unknown: when agents see different certificates at one endpoint,
    /// none is chosen. Only fresh positive evidence is compared.
    /// </summary>
    private string? Ambiguity(AgentSession session, IReadOnlyList<NetworkTarget> targets)
    {
        var now = clock.GetUtcNow();
        foreach (var target in targets.Where(t => t is { Protocol: "tls", Trust: "insecure" }))
        {
            var seen = new List<(string Agent, string Leaf)>();
            foreach (var other in connections.All.Append(session).Distinct())
            {
                lock (other.Reachability.Gate)
                {
                    seen.AddRange(other.Reachability.Entries.Values
                        .Where(e => e.Target.SameEndpoint(target) && Usable(e, now) && e.LeafSha256 is not null)
                        .Select(e => (other.Name, e.LeafSha256!)));
                }
            }

            var distinct = seen.Select(s => s.Leaf).Distinct().ToList();
            if (distinct.Count > 1)
                return $"{target.Label}: agents see different certificates there ({string.Join(", ", seen.Distinct().Select(s => $"{s.Agent}: {s.Leaf[..16]}"))}); "
                       + "with verifyTls off nothing tells which is the installation, so none is chosen";
        }

        return null;
    }

    /// <summary>A session's evidence, for the API.</summary>
    public IReadOnlyList<ReachabilityEntry> Snapshot(AgentSession session)
    {
        var now = clock.GetUtcNow();
        var state = session.Reachability;
        lock (state.Gate)
        {
            return state.Entries.Values
                .Where(e => e.OutstandingProbeId is not null || now - e.LastWanted < Forget)
                .OrderBy(e => e.Target.Kind).ThenBy(e => e.Target.Endpoint, StringComparer.Ordinal).ThenBy(e => e.Target.Context)
                .Select(e => new ReachabilityEntry(e.Target.Kind, e.Target.Endpoint, e.Target.Context,
                    NetworkTarget.InContainer(e.Target.Context, session.ToolRuntime) ? e.Target.Image : null,
                    e.State, e.Detail, e.CheckedAt, e.State == Reachable ? e.CheckedAt + Expiry : null, Usable(e, now),
                    e.OutstandingProbeId is not null, e.LeafSha256))
                .ToList();
        }
    }

    /// <summary>
    /// Keeps evidence about the installations' API endpoints current for every agent that reports reachability: the latest
    /// observation context of each installation whose revision is still eligible and trusted, as dispatch would check
    /// it. Guests are never probed this way; only queued work asks about them.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var sessions = connections.All.Where(s => s.ReportsReachability).ToList();
        if (sessions.Count == 0)
            return;
        var targets = new List<NetworkTarget>();
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Data.OctopushyDb>();
            targets.AddRange(await NetworkRequirements.InstallationTargetsAsync(db, cancellationToken));
        }

        foreach (var session in sessions)
        {
            foreach (var target in targets)
                await CheckOneAsync(session, target, cancellationToken);
        }
    }

    /// <summary>Released when an agent that reports reachability connects, so its first probes need not wait for the minute.</summary>
    internal OrchestratorSignal Connected { get; } = new();

    public void OnConnected(AgentSession session)
    {
        if (session.ReportsReachability)
            Connected.Wake();
    }

    private static string Truncate(string text, int max)
    {
        var clean = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return clean.Length <= max ? clean : clean[..(max - 1)] + "…";
    }
}

/// <summary>Probes installation endpoints every minute and when an agent connects.</summary>
public sealed class ReachabilityRefresher(WorkerReachability reachability, ILogger<ReachabilityRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await reachability.RefreshAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Refreshing reachability failed; it will try again.");
            }

            try
            {
                await reachability.Connected.WaitAsync(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
