using System.Collections.Concurrent;
using System.Threading.Channels;
using Octopushy.Agent.V1;

namespace Octopushy.Server.Agents;

/// <summary>One connected agent's stream: messages to it go through a bounded queue.</summary>
public sealed class AgentSession(Guid agentId, string name, IReadOnlyList<string> executors)
{
    private readonly Channel<ServerMessage> _outbound = Channel.CreateBounded<ServerMessage>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    public Guid Id { get; } = Guid.NewGuid();

    public Guid AgentId { get; } = agentId;

    public string Name { get; } = name;

    public IReadOnlyList<string> Executors { get; } = executors;

    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    public ChannelReader<ServerMessage> Outbound => _outbound.Reader;

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
