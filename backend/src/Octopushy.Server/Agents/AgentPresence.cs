using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Notifications;

namespace Octopushy.Server.Agents;

/// <summary>
/// Tells when an agent goes offline and when it is back. An agent is offline once it is enabled, has connected at least once,
/// is not connected now, and has not been seen for <see cref="OfflineAfter"/>, counted from when this server started at the
/// earliest. An agent that reconnects within that time, as one does on a deploy, a restart or a network blip, is never told.
/// One told offline is told online once when it connects again. See docs/notifications.md.
/// <para>
/// Called once a minute by the orchestrator, outside the engine's lock: it writes only the agents' <see
/// cref="AgentRecord.OfflineNotifiedAt"/> and the notifications, and no run, step or attempt.
/// </para>
/// </summary>
public sealed class AgentPresence(IServiceScopeFactory scopes, AgentConnections connections, TimeProvider clock, ILogger<AgentPresence> logger)
{
    /// <summary>How long an agent may be away before it is told offline.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// When this server started: no agent could be connected before, so none is offline for less than <see cref="OfflineAfter"/>
    /// after it.
    /// </summary>
    private readonly DateTimeOffset _started = clock.GetUtcNow();

    public async Task CheckAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OctopushyDb>();
        var now = clock.GetUtcNow();
        var away = now - OfflineAfter;
        var started = _started;
        // Only what could turn: those told offline, and enabled ones not seen lately.
        var agents = await db.Agents
            .Where(a => a.OfflineNotifiedAt != null
                || !a.Disabled && a.ConnectedAt != null && a.LastSeenAt < away && started < away)
            .ToListAsync(ct);
        foreach (var agent in agents)
        {
            var connected = connections.IsConnected(agent.Id);
            // An agent its update is restarting is away on purpose: the update's deadline tells if it does not come back.
            if (agent.OfflineNotifiedAt is null && !connected && !(agent.UpdateState == AgentUpdateStates.Updating && agent.UpdateReleasedAt != null))
            {
                agent.OfflineNotifiedAt = now;
                NotificationHub.AgentTurned(db, agent, online: false, now);
                logger.LogInformation("Agent {Name} ({Agent}) has been offline for {Minutes} minutes.", agent.Name, agent.Id, (int)OfflineAfter.TotalMinutes);
            }
            else if (agent.OfflineNotifiedAt is not null && connected)
            {
                agent.OfflineNotifiedAt = null;
                NotificationHub.AgentTurned(db, agent, online: true, now);
            }
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
    }
}
