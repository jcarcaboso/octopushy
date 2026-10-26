using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Octopushy.Definitions;
using Octopushy.Server.Api;

namespace Octopushy.Tests.Server;

// Requires the existing disposable fixture; never point this at a retained engine/database.
[Collection(PostgresCollection.Name)]
public class WorkerPreparationTests(PostgresFixture postgres)
{
    private const string Path = "/api/managed-worker-preparation";

    [Fact]
    public async Task Preparation_is_authenticated_read_only_and_exports_a_non_executable_review_artifact()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        using var anonymous = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Path, Selection(Guid.NewGuid()))).StatusCode);
        using var client = await TrustedRevisionTests.AuthenticateAsync(server);
        Assert.Null((await client.GetFromJsonAsync<WorkerPreparationDto>(Path))!.Revision);
        await server.SyncAsync();
        var options = (await client.GetFromJsonAsync<WorkerPreparationDto>(Path))!;
        Assert.True(options.Revision!.IsCurrentSource);
        Assert.Null(options.Revision.TrustedAt);
        Assert.Null(options.PreparationBlockedReason);
        Assert.False(Assert.Single(options.Installations).VerifyTls);
        Assert.False(options.Installations[0].SharedToken);
        Assert.Equal("https://10.1.0.10:8006/", options.Installations[0].Hosts[0].Endpoint);
        Assert.NotEmpty(options.DeploymentGates);
        var runsBefore = await client.GetStringAsync("/api/runs");
        var agentsBefore = await client.GetStringAsync("/api/agents");
        var tokensBefore = await client.GetStringAsync("/api/agents/tokens");
        var response = await client.PostAsJsonAsync(Path, Selection(options.Revision.Id) with { Sockets = 2, Cores = 3, Vlan = 42 });
        response.EnsureSuccessStatusCode();
        var draft = (await response.Content.ReadFromJsonAsync<WorkerDraftDto>())!;
        Assert.Equal("worker-01.managed-worker-preparation.json", draft.FileName);
        var content = JsonNode.Parse(draft.Content)!;
        Assert.Equal("octopushy-managed-worker-preparation", content["kind"]!.GetValue<string>());
        Assert.True(content["preparationOnly"]!.GetValue<bool>());
        Assert.Equal(options.Revision.Id, content["baseRevision"]!["id"]!.GetValue<Guid>());
        Assert.Null(content["repositorySource"]!["url"]);
        Assert.NotNull(content["repositorySource"]!["id"]);
        Assert.Equal("main", content["repositorySource"]!["branch"]!.GetValue<string>());
        Assert.Null(content["configuredEndpoint"]);
        Assert.False(content["proxmoxVerifyTls"]!.GetValue<bool>());
        Assert.Equal("split", content["credentialMode"]!.GetValue<string>());
        Assert.Equal(2, content["guest"]!["hardware"]!["sockets"]!.GetValue<int>());
        Assert.Equal(3, content["guest"]!["hardware"]!["cores"]!.GetValue<int>());
        Assert.Equal(2048, content["guest"]!["hardware"]!["memory"]!.GetValue<int>());
        Assert.Equal(16, content["guest"]!["hardware"]!["disks"]![1]!["size"]!.GetValue<int>());
        Assert.Equal(42, content["guest"]!["network"]!["vlan"]!.GetValue<int>());
        Assert.Null(content["guest"]!["provision"]);
        Assert.DoesNotContain("privateKey", draft.Content);
        Assert.DoesNotContain("127.0.0.1", draft.Content);
        Assert.Equal(runsBefore, await client.GetStringAsync("/api/runs"));
        Assert.Equal(agentsBefore, await client.GetStringAsync("/api/agents"));
        Assert.Equal(tokensBefore, await client.GetStringAsync("/api/agents/tokens"));
        Assert.Null((await client.GetFromJsonAsync<RepositoryDto>("/api/repository"))!.Revision!.TrustedAt);

        server.Repository.Write($"docs/worker-drafts/{draft.FileName}", draft.Content);
        Assert.Single(LabRepositoryLoader.Load(server.Repository.Root).Guests);
        // Even an accidental rename must not produce a provisionable ordinary guest.
        server.Repository.Write("labs/home/proxmox/korriban/guests/worker-01/guest.yaml", draft.Content);
        var loaded = LabRepositoryLoader.Load(server.Repository.Root);
        Assert.False(loaded.IsValid("home/korriban/worker-01"));
        Assert.Null(loaded.Guest("home/korriban/worker-01"));
        Assert.True(loaded.IsValid("home/korriban/krb-app-01"));
    }

    [Fact]
    public async Task Validation_rejects_unconfigured_targets_collisions_bad_hardware_and_extra_authority_fields()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        using var client = await TrustedRevisionTests.AuthenticateAsync(server);
        await server.SyncAsync();
        var options = (await client.GetFromJsonAsync<WorkerPreparationDto>(Path))!;
        var valid = Selection(options.Revision!.Id);
        WorkerDraftRequest[] invalid =
        [
            valid with { Installation = "https://attacker.invalid" },
            valid with { Host = "unconfigured-host" },
            valid with { Host = null! },
            valid with { Name = "../escape" },
            valid with { Name = "worker\n" },
            valid with { Name = null! },
            valid with { Name = "krb-app-01" },
            valid with { Vmid = 310 },
            valid with { Vmid = 99 },
            valid with { Vmid = 1000000000 },
            valid with { Sockets = 0 },
            valid with { Sockets = 17 },
            valid with { Sockets = 16, Cores = 9 },
            valid with { Cores = int.MaxValue },
            valid with { MemoryMiB = 511 },
            valid with { BootDiskGiB = 7 },
            valid with { StateDiskGiB = 15 },
            valid with { Datastore = "other-host-disk" },
            valid with { Bridge = "other-host-bridge" },
            valid with { Vlan = 0 },
            valid with { Vlan = 4095 },
        ];
        foreach (var request in invalid)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, request)).StatusCode);
        }
        foreach (var field in new[] { "endpoint", "command", "sshPrivateKey", "agentUrl" })
        {
            var payload = JsonSerializer.SerializeToNode(valid, JsonSerializerOptions.Web)!.AsObject();
            payload[field] = "caller-controlled";
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, payload)).StatusCode);
        }
        var fractional = JsonSerializer.SerializeToNode(valid, JsonSerializerOptions.Web)!.AsObject();
        fractional["cores"] = 1.5;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, fractional)).StatusCode);
    }

    [Fact]
    public async Task Stale_heads_previous_sources_and_disconnected_sources_cannot_be_used_for_preparation()
    {
        await using var server = await ServerHarness.StartAsync(postgres, withSecrets: false);
        using var client = await TrustedRevisionTests.AuthenticateAsync(server);
        await server.SyncAsync();
        var original = (await client.GetFromJsonAsync<WorkerPreparationDto>(Path))!;
        var selection = Selection(original.Revision!.Id);
        server.Repository.Write("notes.txt", "New head");
        server.Commit("Next source revision");
        await server.SyncAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, selection)).StatusCode);
        var current = (await client.GetFromJsonAsync<WorkerPreparationDto>(Path))!;
        selection = selection with { RevisionId = current.Revision!.Id };
        (await client.PutAsJsonAsync("/api/repository",
            new RepositoryUpdate(server.Repository.Root + "/.", "main", 60, "none", null, null))).EnsureSuccessStatusCode();
        var changed = (await client.GetFromJsonAsync<WorkerPreparationDto>(Path))!;
        Assert.Null(changed.Revision);
        Assert.Empty(changed.Installations);
        Assert.NotNull(changed.PreparationBlockedReason);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, selection)).StatusCode);
        await server.SyncAsync();
        var reconnected = (await client.GetFromJsonAsync<WorkerPreparationDto>(Path))!;
        Assert.Equal(current.Revision.Commit, reconnected.Revision!.Commit);
        Assert.NotEqual(current.Revision.Id, reconnected.Revision.Id);
        Assert.Null(reconnected.Revision.TrustedAt);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, selection)).StatusCode);
        (await client.PostAsJsonAsync(Path, selection with { RevisionId = reconnected.Revision.Id })).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/repository",
            new RepositoryUpdate(null, "main", 60, "none", null, null))).EnsureSuccessStatusCode();
        Assert.Empty((await client.GetFromJsonAsync<WorkerPreparationDto>(Path))!.Installations);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, selection with { RevisionId = reconnected.Revision.Id })).StatusCode);
    }

    [Fact]
    public async Task Host_choices_and_routes_come_from_the_selected_installation_and_preserve_shared_credentials()
    {
        using var repository = TestRepository.Minimal();
        var file = "labs/home/proxmox/korriban/installation.yaml";
        var text = File.ReadAllText(System.IO.Path.Combine(repository.Root, file))
            .Replace("type: standalone", "type: cluster")
            .Replace("readOnlyToken: { store: proxmox/korriban/read-only-token }\n  privilegedToken: { store: proxmox/korriban/privileged-token }",
                "token: { store: proxmox/korriban/token }");
        repository.Write(file, text + """

              spare:
                endpoint: https://spare.invalid:8006/
                datastores: { disks: spare-disk }
                bridges: [spare-bridge]
            """);
        await using var server = await ServerHarness.StartAsync(postgres, repository, withSecrets: false);
        using var client = await TrustedRevisionTests.AuthenticateAsync(server);
        await server.SyncAsync();
        var options = (await client.GetFromJsonAsync<WorkerPreparationDto>(Path))!;
        var installation = Assert.Single(options.Installations);
        Assert.True(installation.SharedToken);
        Assert.Equal(2, installation.Hosts.Count);
        Assert.Equal("https://spare.invalid:8006/", installation.Hosts.Single(h => h.Id == "spare").Endpoint);
        var valid = Selection(options.Revision!.Id) with { Host = "spare", Datastore = "spare-disk", Bridge = "spare-bridge" };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, valid with { Datastore = "local-lvm" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, valid with { Bridge = "vmbr0" })).StatusCode);
        var response = await client.PostAsJsonAsync(Path, valid);
        response.EnsureSuccessStatusCode();
        var draft = (await response.Content.ReadFromJsonAsync<WorkerDraftDto>())!;
        Assert.Equal("spare", JsonNode.Parse(draft.Content)!["guest"]!["host"]!.GetValue<string>());
        Assert.Equal("shared", JsonNode.Parse(draft.Content)!["credentialMode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Missing_host_configuration_and_invalid_repository_settings_never_fall_back_to_implicit_targets()
    {
        using var repository = TestRepository.Minimal();
        var file = "labs/home/proxmox/korriban/installation.yaml";
        repository.Write(file, File.ReadAllText(System.IO.Path.Combine(repository.Root, file))
            .Replace("datastores: { images: local, disks: local-lvm }", "datastores: {}")
            .Replace("bridges: [vmbr0]", "bridges: []"));
        await using var server = await ServerHarness.StartAsync(postgres, repository, withSecrets: false);
        using var client = await TrustedRevisionTests.AuthenticateAsync(server);
        await server.SyncAsync();
        var options = (await client.GetFromJsonAsync<WorkerPreparationDto>(Path))!;
        var host = Assert.Single(Assert.Single(options.Installations).Hosts);
        Assert.Empty(host.Datastores);
        Assert.Empty(host.Bridges);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, Selection(options.Revision!.Id))).StatusCode);
        repository.Write("octopushy.yaml", "schemaVersion: 99");
        server.Commit("Invalid repository settings");
        await server.SyncAsync();
        var invalid = (await client.GetFromJsonAsync<WorkerPreparationDto>(Path))!;
        Assert.NotNull(invalid.PreparationBlockedReason);
        Assert.Empty(invalid.Installations);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Path, Selection(invalid.Revision!.Id))).StatusCode);
    }

    private static WorkerDraftRequest Selection(Guid revision) =>
        new(revision, "home/korriban", "korriban", "worker-01", 901, 1, 2, 2048, 8, 16, "local-lvm", "vmbr0", null);
}
