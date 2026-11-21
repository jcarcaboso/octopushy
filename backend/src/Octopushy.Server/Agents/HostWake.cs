using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Octopushy.Agent.V1;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Agents;

/// <summary>
/// Wake-on-LAN for a Proxmox host whose <c>installation.yaml</c> gives it <c>wakeOnLan</c>. An operator asks; every connected
/// agent announcing <see cref="Capability"/> sends the magic packet, since none can tell whether it shares the host's network
/// segment. Nothing comes back: the host's next observation says whether it woke.
/// </summary>
public static class HostWake
{
    public const string Capability = "wake-on-lan-v1";

    public const string DefaultBroadcast = "255.255.255.255";

    /// <summary>The host's MAC address as the definition writes it, or null when it has no <c>wakeOnLan</c>.</summary>
    public static string? Mac(JsonNode? definition, string host) =>
        definition?["hosts"]?[host]?["wakeOnLan"]?["mac"]?.GetValue<string>();

    /// <summary>Sends the packet for <paramref name="host"/> through every agent that can; returns how many it went to.</summary>
    public static async Task<int> WakeAsync(OctopushyDb db, AgentConnections connections, Guid installationId, string host,
        CancellationToken cancellationToken)
    {
        var resource = await db.Resources.AsNoTracking()
                           .FirstOrDefaultAsync(r => r.Id == installationId && r.Kind == ResourceKinds.Installation && r.RetiredAt == null, cancellationToken)
                       ?? throw new RunException("There is no such installation.");
        if (!resource.Valid || resource.MissingSince is not null || resource.Definition is null)
            throw new RunException("Fix the installation's definition before waking its hosts.");
        // Rows stay when the repository source changes or is disconnected: only the current source's definition counts.
        if (!await RepositoryTrust.EligibleRevisions(db).AnyAsync(r => r.Id == resource.LastRevisionId, cancellationToken))
            throw new RunException("The installation is not in the current repository's valid revisions.");
        if (resource.ObservedOnly)
            throw new RunException("This installation is observed only: Octopushy changes nothing in it, and wakes none of its hosts.");
        var trust = await db.InstallationTrusts.AsNoTracking().FirstOrDefaultAsync(t => t.Address == resource.Address, cancellationToken);
        if (Views.Trust(resource, trust).Untrusted.Count > 0)
            throw new RunException("Trust the installation's endpoints before waking its hosts.");

        var definition = JsonNode.Parse(resource.Definition);
        var settings = definition?["hosts"]?[host]?["wakeOnLan"];
        if (Mac(definition, host) is not { } mac)
            throw new RunException($"The installation declares no wakeOnLan for the host {host}.");
        var broadcast = settings?["broadcast"]?.GetValue<string>() ?? DefaultBroadcast;
        if (!IPAddress.TryParse(broadcast, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new RunException($"The wakeOnLan broadcast of the host {host} is not an IPv4 address.");

        var message = new ServerMessage
        {
            WakeOnLan = new WakeOnLan
            {
                Mac = ByteString.CopyFrom(Convert.FromHexString(mac.Replace(":", ""))),
                Broadcast = address.ToString(),
            },
        };
        var sent = connections.All.Where(s => s.Capabilities.Contains(Capability)).Count(s => s.TrySend(message));
        if (sent == 0)
            throw new RunException("No connected agent can send Wake-on-LAN packets. Update an agent on the host's network.");
        return sent;
    }
}
