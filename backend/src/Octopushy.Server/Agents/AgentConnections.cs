using System.Collections.Concurrent;
using System.Threading.Channels;
using Octopushy.Agent.V1;

namespace Octopushy.Server.Agents;

/// <summary>One connected agent's stream: messages to it go through a bounded queue.</summary>
/// <param name="toolRuntime">How the agent runs tools, as its Hello said: <c>local</c>, <c>podman</c> or <c>docker</c>.</param>
public sealed class AgentSession(Guid agentId, string name, IReadOnlyList<string> executors, IReadOnlyList<string>? capabilities = null,
    string? toolRuntime = null)
{
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

    public IReadOnlyList<string> Executors { get; } = executors;

    public IReadOnlyList<string> Capabilities { get; } = capabilities ?? [];

    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    public string? ToolRuntime { get; } = toolRuntime;

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
    public bool TrySend(ServerMessage message) => _outbound.Writer.TryWrite(message);

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

    public void Close() => _outbound.Writer.TryComplete();
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
