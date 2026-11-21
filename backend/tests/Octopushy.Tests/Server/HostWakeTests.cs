using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;

namespace Octopushy.Tests.Server;

/// <summary>Waking a Proxmox host whose installation.yaml gives it <c>wakeOnLan</c>.</summary>
public class HostWakeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static TestRepository Repository(string wakeOnLan)
    {
        var repository = TestRepository.Minimal();
        repository.Write("labs/home/proxmox/korriban/installation.yaml", $$"""
            schemaVersion: 1
            type: standalone
            connection:
              endpoint: https://10.1.0.10:8006/
              tls: { verify: false }
              readOnlyToken: { store: proxmox/korriban/read-only-token }
              privilegedToken: { store: proxmox/korriban/privileged-token }
            hosts:
              korriban:
                datastores: { images: local, disks: local-lvm }
                bridges: [vmbr0]
            {{wakeOnLan}}
            """);
        return repository;
    }

    private static AgentSession Connect(ServerHarness server, string name, params string[] capabilities)
    {
        var session = new AgentSession(Guid.NewGuid(), name, ["proxmox"], capabilities);
        server.Services.GetRequiredService<AgentConnections>().Add(session);
        return session;
    }

    private static List<WakeOnLan> Wakes(AgentSession session)
    {
        var wakes = new List<WakeOnLan>();
        while (session.Outbound.TryRead(out var message))
            if (message.KindCase == ServerMessage.KindOneofCase.WakeOnLan) wakes.Add(message.WakeOnLan);
        return wakes;
    }

    [Fact]
    public async Task Every_agent_that_can_sends_the_packet()
    {
        using var repository = Repository("""
                wakeOnLan: { mac: "A8:a1:59:3c:0e:72", broadcast: 10.1.0.255 }
            """);
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        var client = await server.SignedInClientAsync();
        await server.SyncAndTrustAsync(client);
        var installation = await server.ResourceAsync("home/korriban");
        var lan = Connect(server, "lan", HostWake.Capability);
        var other = Connect(server, "other", HostWake.Capability);
        var old = Connect(server, "old");

        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/installations/{installation.Id}"))!;
        Assert.Equal("A8:a1:59:3c:0e:72", detail["installation"]!["hosts"]![0]!["wakeOnLanMac"]!.GetValue<string>());

        var response = await client.PostAsync($"/api/installations/{installation.Id}/hosts/korriban/wake", null);
        response.EnsureSuccessStatusCode();
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonObject>())!["agents"]!.GetValue<int>());
        foreach (var session in new[] { lan, other })
        {
            var wake = Assert.Single(Wakes(session));
            Assert.Equal(new byte[] { 0xa8, 0xa1, 0x59, 0x3c, 0x0e, 0x72 }, wake.Mac.ToByteArray());
            Assert.Equal("10.1.0.255", wake.Broadcast);
        }

        Assert.Empty(Wakes(old));
    }

    [Fact]
    public async Task A_host_without_wakeOnLan_or_an_agent_that_can_is_refused()
    {
        using var repository = Repository("""
                wakeOnLan: { mac: "a8:a1:59:3c:0e:72" }
            """);
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        var client = await server.SignedInClientAsync();
        await server.SyncAndTrustAsync(client);
        var installation = await server.ResourceAsync("home/korriban");

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/installations/{installation.Id}/hosts/korriban/wake", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/installations/{installation.Id}/hosts/moraband/wake", null)).StatusCode);

        var lan = Connect(server, "lan", HostWake.Capability);
        (await client.PostAsync($"/api/installations/{installation.Id}/hosts/korriban/wake", null)).EnsureSuccessStatusCode();
        Assert.Equal(HostWake.DefaultBroadcast, Assert.Single(Wakes(lan)).Broadcast);

        // The row stays when the repository source changes; its definition no longer counts.
        await server.WithDbAsync(async db =>
        {
            (await db.RepositorySettings.SingleAsync()).SourceId = Guid.NewGuid();
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/installations/{installation.Id}/hosts/korriban/wake", null)).StatusCode);
        Assert.Empty(Wakes(lan));
    }
}
