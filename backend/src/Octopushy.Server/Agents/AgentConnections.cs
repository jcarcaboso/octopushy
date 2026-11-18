using System.Collections.Concurrent;
using System.Threading.Channels;
using Octopushy.Agent.V1;

namespace Octopushy.Server.Agents;

/// <summary>One connected agent's stream: messages to it go through a bounded queue.</summary>
/// <param name="toolRuntime">How the agent runs tools, as its Hello said: <c>local</c>, <c>podman</c> or <c>docker</c>.</param>
/// <param name="unavailable">Why each executor the agent knows and does not offer is missing, cleaned: see <see cref="AgentAvailability"/>.</param>
public sealed class AgentSession(Guid agentId, string name, IReadOnlyList<string> executors, IReadOnlyList<string>? capabilities = null,
    string? toolRuntime = null, IReadOnlyDictionary<string, string>? unavailable = null)
{
    private sealed record Offer(IReadOnlyList<string> Executors, IReadOnlyDictionary<string, string> Unavailable);

    private volatile Offer _offer = new(executors, unavailable ?? new SortedDictionary<string, string>());
    private volatile AgentStorage? _storage;

    /// <summary>The operator-only ability to stop a running mutating tool.</summary>
    public const string ForcedMutationStopCapability = "forced-mutation-stop-v1";

    public bool SupportsForcedMutationStop => Capabilities.Contains(ForcedMutationStopCapability);

    /// <summary>The outbound capacity, shared by assignments, cancels, acknowledgements and probes.</summary>
    public const int OutboundCapacity = 256;

    private readonly Channel<ServerMessage> _outbound = Channel.CreateBounded<ServerMessage>(new BoundedChannelOptions(OutboundCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    public Guid Id { get; } = Guid.NewGuid();

    public Guid AgentId { get; } = agentId;

    public string Name { get; } = name;

    /// <summary>
    /// The executors the agent offers now: from its Hello, then from its last <c>ExecutorsChanged</c>. Dispatch reads this,
    /// so a change takes effect on the open connection.
    /// </summary>
    public IReadOnlyList<string> Executors => _offer.Executors;

    /// <summary>Why each executor the agent does not offer is missing, as it said last.</summary>
    public IReadOnlyDictionary<string, string> Unavailable => _offer.Unavailable;

    /// <summary>The room the agent reported last on this connection; null until a heartbeat carries it.</summary>
    public AgentStorage? Storage
    {
        get => _storage;
        set => _storage = value;
    }

    // When the next offer change may be logged, woken for and stored (ticks of the server's clock), whether the latest one is not
    // stored yet, and whether a flush of it is queued. Changed only under the engine's gate, except the flush flag.
    private long _offerEffectsAfter = long.MinValue;
    private int _offerUnsaved;
    private int _offerFlushQueued;

    /// <summary>The offer in memory is newer than what the agent's record holds.</summary>
    public bool OfferUnsaved
    {
        get => Volatile.Read(ref _offerUnsaved) == 1;
        set => Volatile.Write(ref _offerUnsaved, value ? 1 : 0);
    }

    /// <summary>Before this, a change of the offer is applied in memory only: no log line, no wake, no write.</summary>
    public DateTimeOffset OfferEffectsAfter
    {
        get => Volatile.Read(ref _offerEffectsAfter) is var t && t == long.MinValue ? DateTimeOffset.MinValue : new DateTimeOffset(t, TimeSpan.Zero);
        set => Volatile.Write(ref _offerEffectsAfter, value.UtcTicks);
    }

    /// <summary>
    /// Whether an unsaved offer is due to be stored at <paramref name="now"/> and no flush is queued yet; if so, a flush counts
    /// as queued until <see cref="OfferFlushTaken"/>.
    /// </summary>
    public bool TryQueueOfferFlush(DateTimeOffset now) =>
        OfferUnsaved && now >= OfferEffectsAfter && Interlocked.CompareExchange(ref _offerFlushQueued, 1, 0) == 0;

    public void OfferFlushTaken() => Volatile.Write(ref _offerFlushQueued, 0);

    /// <summary>
    /// Replaces what the agent offers. Returns false when nothing changed. <paramref name="executors"/> is a set as
    /// <see cref="AgentAvailability.Executors"/> makes it. Called under the engine's gate, which dispatch holds from reading
    /// <see cref="Executors"/> to handing work over.
    /// </summary>
    public bool SetExecutors(IReadOnlyList<string> executors, IReadOnlyDictionary<string, string> unavailable)
    {
        var current = _offer;
        if (current.Executors.SequenceEqual(executors) && current.Unavailable.Count == unavailable.Count
            && current.Unavailable.All(r => unavailable.TryGetValue(r.Key, out var other) && other == r.Value))
            return false;
        _offer = new Offer(executors, unavailable);
        return true;
    }

    public IReadOnlyList<string> Capabilities { get; } = capabilities ?? [];

    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    public string? ToolRuntime { get; } = toolRuntime;

    /// <summary>
    /// The address the agent connected from, as this server sees it: through NAT, not the agent's own. Null when unknown.
    /// Used only to keep the agent off its own guest's SSH (<see cref="Orchestration.NetworkRequirements.RunsOn"/>).
    /// </summary>
    public System.Net.IPAddress? RemoteAddress { get; init; }

    /// <summary>Whether the agent answers reachability probes (<see cref="WorkerReachability.Capability"/>).</summary>
    public bool ReportsReachability => Capabilities.Contains(WorkerReachability.Capability);

    /// <summary>
    /// What this session's agent was found to reach, kept only as long as the session: a reconnect starts with none.
    /// See <see cref="WorkerReachability"/>.
    /// </summary>
    public SessionReachability Reachability { get; } = new();

    /// <summary>
    /// An agent in this process, as in tests: answers a probe at once instead of over the stream. Never set for a
    /// connected agent.
    /// </summary>
    public Func<ReachabilityProbe, ReachabilityReport>? Responder { get; init; }

    /// <summary>Messages waiting to go out.</summary>
    public int Queued => _outbound.Reader.Count;

    public ChannelReader<ServerMessage> Outbound => _outbound.Reader;

    /// <summary>Nonblocking hand-off, so credential revocation can serialize with dispatch.</summary>
    public bool TrySend(ServerMessage message)
    {
        if (AttemptOf(message) is not { } attempt)
            return _outbound.Writer.TryWrite(message);
        // Queued and noted as not yet written in one step: its write cannot begin in between.
        lock (_writing)
        {
            if (!_outbound.Writer.TryWrite(message))
                return false;
            _unwritten.Add(attempt);
            return true;
        }
    }

    public async Task<bool> SendAsync(ServerMessage message, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await _outbound.Writer.WriteAsync(message, timeout.Token);
            return true;
        }
        catch (Exception e) when (e is OperationCanceledException or ChannelClosedException)
        {
            return false;
        }
    }

    // What has been written and what never was: a message is written only once BeginWrite let it through, under this lock,
    // while the session was open; Close and a cancelled call shut that door under the same lock.
    private readonly Lock _writing = new();
    private bool _closed;
    private bool _reported;
    private readonly List<Guid> _skipped = [];
    private readonly HashSet<Guid> _unwritten = [];

    /// <summary>
    /// Writes what is queued, in order, until the session closes or <paramref name="cancellationToken"/> is cancelled. A message
    /// taken off the queue once either happened is not written, and is reported by <see cref="TakeUnwritten"/>.
    /// </summary>
    public async Task WriteAllAsync(Func<ServerMessage, Task> write, CancellationToken cancellationToken)
    {
        while (await _outbound.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_outbound.Reader.TryRead(out var message))
            {
                if (!BeginWrite(message, cancellationToken))
                    return;
                await write(message);
            }
        }
    }

    /// <summary>
    /// The one boundary between a message that may have left this process and one that certainly did not. True: its write
    /// begins, and from here on whether the agent has it is unknown. False: the session was closed or the call cancelled first,
    /// nothing of it is written, and so neither is anything queued after it.
    /// </summary>
    private bool BeginWrite(ServerMessage message, CancellationToken cancellationToken)
    {
        lock (_writing)
        {
            if (!_closed && !cancellationToken.IsCancellationRequested)
            {
                if (AttemptOf(message) is { } begun)
                    _unwritten.Remove(begun);
                return true;
            }
            _closed = true;
            if (AttemptOf(message) is { } attempt)
                _skipped.Add(attempt);
            return false;
        }
    }

    /// <summary>
    /// Whether the write of an attempt's assignment handed to this session has begun. False while it waits on the queue and
    /// for one this session never wrote, which <see cref="TakeUnwritten"/> reports once the session has closed.
    /// </summary>
    public bool WriteBegan(Guid attemptId)
    {
        lock (_writing)
            return !_unwritten.Contains(attemptId);
    }

    public void Close()
    {
        lock (_writing)
            _closed = true;
        _outbound.Writer.TryComplete();
    }

    /// <summary>
    /// The attempts whose assignment this session never began to write: taken off the queue after it closed, or still on it.
    /// Once, for the caller that ran <see cref="WriteAllAsync"/> and has seen it end; empty for every later call and while the
    /// session is open.
    /// </summary>
    public IReadOnlyList<Guid> TakeUnwritten()
    {
        lock (_writing)
        {
            if (!_closed || _reported)
                return [];
            _reported = true;
            var unwritten = new List<Guid>(_skipped);
            while (_outbound.Reader.TryRead(out var message))
                if (AttemptOf(message) is { } attempt)
                    unwritten.Add(attempt);
            return unwritten;
        }
    }

    private static Guid? AttemptOf(ServerMessage message) =>
        message.Assignment is { } assignment && Guid.TryParse(assignment.AttemptId, out var id) ? id : null;
}

/// <summary>The agents connected to this server right now.</summary>
public sealed class AgentConnections
{
    private readonly ConcurrentDictionary<Guid, AgentSession> _sessions = new();

    public IReadOnlyCollection<AgentSession> All => _sessions.Values.ToList();

    public AgentSession? Get(Guid agentId) => _sessions.GetValueOrDefault(agentId);

    public bool IsConnected(Guid agentId) => _sessions.ContainsKey(agentId);

    /// <summary>Registers a session, closing an older one of the same agent.</summary>
    public void Add(AgentSession session)
    {
        _sessions.AddOrUpdate(session.AgentId, session, (_, previous) =>
        {
            previous.Close();
            return session;
        });
    }

    public void Remove(AgentSession session)
    {
        _sessions.TryRemove(new KeyValuePair<Guid, AgentSession>(session.AgentId, session));
        session.Close();
    }

    /// <summary>Disconnects an agent, for example when it is disabled.</summary>
    public void Disconnect(Guid agentId)
    {
        if (_sessions.TryRemove(agentId, out var session))
        {
            session.Close();
        }
    }
}
