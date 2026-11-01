using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Octopushy.Agent.V1;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Git;
using Octopushy.Server.Inventory;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;
using Octopushy.Tests.Definitions;

namespace Octopushy.Tests.Server;

[Collection(PostgresCollection.Name)]
public class ClusterRunTests(PostgresFixture postgres)
{
    private const string Cluster = ClusterDefinitionTests.Cluster;
    private const string ClusterFile = ClusterDefinitionTests.ClusterFile;
    private const string Password = "a-shared-dns-admin-password";

    private static JsonObject Outputs => new() { ["outputs"] = new JsonObject(), ["sensitiveOutputs"] = new JsonArray() };

    private static JsonObject Inspection(string state = "joined") => new()
    {
        ["formed"] = true,
        ["members"] = new JsonArray(new JsonObject
        {
            ["guest"] = ClusterDefinitionTests.Secondary,
            ["state"] = state,
            ["peerId"] = state == "absent" ? null : 22L,
            ["peerName"] = state == "absent" ? null : "mrb-dns-01.cluster.home.arpa",
        }),
        ["undeclared"] = new JsonObject { ["blocked"] = new JsonArray(), ["allowed"] = new JsonArray() },
    };

    /// <summary>A plan that changes these resources, with the summary the agent reports.</summary>
    private static JsonObject PlanOf(params (string Address, string[] Actions)[] changes)
    {
        int Count(Func<string[], bool> match) => changes.Count(c => match(c.Actions));
        return new JsonObject
        {
            ["hasChanges"] = changes.Length > 0,
            ["changes"] = new JsonArray(changes.Select(c => (JsonNode?)new JsonObject
            {
                ["address"] = c.Address,
                ["type"] = "terraform_data",
                ["name"] = "x",
                ["actions"] = new JsonArray(c.Actions.Select(a => (JsonNode?)a).ToArray()),
            }).ToArray()),
            ["counts"] = new JsonObject
            {
                ["create"] = Count(a => a is ["create"]),
                ["update"] = Count(a => a is ["update"]),
                ["delete"] = Count(a => a is ["delete"]),
                ["replace"] = Count(a => a.Length == 2),
            },
            ["toolVersion"] = "1.13.1",
        };
    }

    /// <summary>What an apply of the cluster's built-in state returns.</summary>
    private static Artifact State()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("""{"version":4,"serial":1,"lineage":"cluster","outputs":{},"resources":[]}""");
        return new Artifact { Name = TofuStateStore.ArtifactName, Content = ByteString.CopyFrom(bytes), Sha256 = ArtifactStore.Sha256(bytes) };
    }

    private static Task ApplyAsync(FakeAgent agent, Assignment apply) =>
        agent.ReportAsync(apply, Outcome.Succeeded, Outputs, artifacts: State());

    private static string Secondary(string guest = ClusterDefinitionTests.Secondary) => $"terraform_data.secondary[\"{guest}\"]";

    private static readonly (string, string[]) Forming = ("technitium_cluster.this", ["create"]);

    /// <summary>Starts a server for the cluster's lab, with both guests applied and verified.</summary>
    private async Task<(ServerHarness Server, FakeAgent Agent, HttpClient Client)> ReadyAsync(TestRepository repository, bool trust = true)
    {
        var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SetSecretAsync("dns/admin", Password);
        var agent = await server.ConnectAgentAsync("home");
        var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        await FinishGuestsAsync(server, agent);
        if (trust)
            await TrustAsync(server, agent, client);
        return (server, agent, client);
    }

    /// <summary>
    /// Answers guest work until the guests are verified. Both run at once, so this answers
    /// whatever arrives. <paramref name="failing"/> names a guest whose plan fails.
    /// </summary>
    private static async Task FinishGuestsAsync(ServerHarness server, FakeAgent agent, int guests = 2, string? failing = null)
    {
        var verified = 0;
        for (var i = 0; i < 40 && verified < guests; i++)
        {
            await server.TickAsync();
            foreach (var assignment in agent.TakeAssignments())
            {
                switch (assignment.Action)
                {
                    case "observe":
                        await agent.ReportAsync(assignment, Outcome.Succeeded, new JsonObject());
                        break;
                    case "preview" when failing is not null && assignment.InputJson.Contains(failing):
                        await agent.ReportAsync(assignment, Outcome.Failed, error: "the host is off");
                        break;
                    case "preview":
                        await agent.ReportAsync(assignment, Outcome.Succeeded, FakeAgent.Plan(create: 1), artifacts: FakeAgent.PlanArtifact());
                        break;
                    case "apply":
                        await agent.ReportAsync(assignment, Outcome.Succeeded, Outputs);
                        break;
                    case "verify-guest":
                        await agent.ReportAsync(assignment, Outcome.Succeeded, new JsonObject { ["present"] = true });
                        verified++;
                        break;
                }
            }
        }

        Assert.Equal(guests, verified);
        await server.TickAsync();
    }

    private static async Task<JsonObject> ClusterSummaryAsync(HttpClient client)
    {
        var inventory = (await client.GetFromJsonAsync<JsonObject>("/api/inventory"))!;
        return inventory["labs"]![0]!["clusters"]![0]!.AsObject();
    }

    private const string PrimaryEndpoint = "https://10.1.0.32:53443";
    private const string SecondaryEndpoint = "https://10.1.0.42:53443";

    private static readonly Dictionary<(string, int), (string Sha256, string Pem)> Issued = [];

    /// <summary>A real self-signed certificate for a member; another generation is another certificate.</summary>
    private static (string Sha256, string Pem) Issue(string endpoint, int generation = 0)
    {
        lock (Issued)
        {
            if (Issued.TryGetValue((endpoint, generation), out var known))
                return known;
            using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest($"CN={new Uri(endpoint).Host}-{generation}", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            return Issued[(endpoint, generation)] = (
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(certificate.RawData)),
                new string(System.Security.Cryptography.PemEncoding.Write("CERTIFICATE", certificate.RawData)));
        }
    }

    private static string Sha(string endpoint, int generation = 0) => Issue(endpoint, generation).Sha256;

    private static JsonObject Certificate(string endpoint, int generation = 0) => new()
    {
        ["endpoint"] = endpoint,
        ["sha256"] = Issue(endpoint, generation).Sha256,
        ["pem"] = Issue(endpoint, generation).Pem,
    };

    private static async Task<HttpResponseMessage> PostTrustAsync(HttpClient client, JsonObject cluster)
    {
        var shown = new JsonObject();
        foreach (var certificate in cluster["trust"]!["certificates"]!.AsArray())
            if (certificate!["sha256"] is { } sha256)
                shown[certificate["endpoint"]!.GetValue<string>()] = sha256.GetValue<string>();
        return await client.PostAsJsonAsync($"/api/clusters/{cluster["id"]}/trust", new
        {
            endpoints = cluster["trust"]!["endpoints"],
            codeDigest = cluster["trust"]!["codeDigest"]!.GetValue<string>(),
            certificates = shown,
        });
    }

    /// <summary>Has the agent read what the members present, as the page asks it to.</summary>
    private static async Task ReadCertificatesAsync(ServerHarness server, FakeAgent agent, HttpClient client, params JsonObject[] seen)
    {
        var id = (await ClusterSummaryAsync(client))["id"]!.GetValue<Guid>();
        (await client.PostAsync($"/api/clusters/{id}/certificates", null)).EnsureSuccessStatusCode();
        var read = await agent.NextKeepingOthersAsync("certificates");
        Assert.Empty(read.Secrets);
        await agent.ReportAsync(read, Outcome.Succeeded, new JsonObject
        {
            ["certificates"] = new JsonArray(seen.Select(c => (JsonNode?)c.DeepClone()).ToArray()),
        });
        await server.TickAsync();
    }

    private static async Task TrustAsync(ServerHarness server, FakeAgent agent, HttpClient client)
    {
        await ReadCertificatesAsync(server, agent, client, Certificate(PrimaryEndpoint), Certificate(SecondaryEndpoint));
        (await PostTrustAsync(client, await ClusterSummaryAsync(client))).EnsureSuccessStatusCode();
        await server.TickAsync();
    }

    /// <summary>
    /// The next verification, past the download a run does while its lists are not known to be
    /// downloaded, and any background check it finds on the way.
    /// </summary>
    private static async Task<Assignment> VerifyAsync(ServerHarness server, FakeAgent agent)
    {
        for (var i = 0; i < 6; i++)
        {
            await server.TickAsync();
            foreach (var assignment in agent.TakeAssignments())
            {
                if (assignment.Action == "verify")
                    return assignment;
                if (assignment.Action == "refresh-block-lists")
                    await agent.ReportAsync(assignment, Outcome.Succeeded, new JsonObject { ["refreshed"] = true });
            }
        }

        throw new InvalidOperationException("No verify assignment arrived.");
    }

    /// <summary>Takes a run through inspect and a plan of these changes.</summary>
    private static async Task<Assignment> PlanAsync(FakeAgent agent, JsonObject plan, JsonObject? inspection = null)
    {
        await agent.ReportAsync(await agent.NextAsync("inspect"), Outcome.Succeeded, inspection ?? Inspection());
        var preview = await agent.NextAsync("preview");
        if (plan["hasChanges"]!.GetValue<bool>())
            await agent.ReportAsync(preview, Outcome.Succeeded, plan, artifacts: FakeAgent.PlanArtifact());
        else
            await agent.ReportAsync(preview, Outcome.Succeeded, plan);
        return preview;
    }

    [Fact]
    public async Task A_new_cluster_waits_for_trust_and_receives_no_password()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository, trust: false);
        await using var _ = server;
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains(RepositoryTrust.WaitingForCluster, run.Detail);
        Assert.Contains("https://10.1.0.42:53443", run.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor == "technitium");
        Assert.Equal(0, await server.QueryAsync(db => db.SecretResolutions.CountAsync(r => r.Key == "dns/admin" && db.Attempts.Any(a => a.Id == r.AttemptId && a.Executor == "technitium"))));

        var cluster = await ClusterSummaryAsync(client);
        Assert.Equal(2, cluster["trust"]!["untrusted"]!.AsArray().Count);

        // Trusting before the members can be read leaves their certificates to trust.
        Assert.Equal(HttpStatusCode.OK, (await PostTrustAsync(client, cluster)).StatusCode);
        await server.TickAsync();
        Assert.Contains("no certificate is trusted yet", (await server.LatestRunAsync(Cluster, Operations.Cluster)).Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Secrets.ContainsKey(SecretNames.ClusterPassword));

        await TrustAsync(server, agent, client);
        await agent.NextAsync("inspect");
    }

    [Fact]
    public async Task The_password_travels_only_with_the_certificates_an_operator_trusted()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var _ = server;

        var inspect = await agent.NextAsync("inspect");
        var members = JsonNode.Parse(inspect.InputJson)!["members"]!.AsArray();
        Assert.Equal(Sha(PrimaryEndpoint), members[0]!["certificateSha256"]!.GetValue<string>());
        Assert.Equal(Sha(SecondaryEndpoint), members[1]!["certificateSha256"]!.GetValue<string>());
        await agent.ReportAsync(inspect, Outcome.Succeeded, Inspection("absent"));

        var preview = JsonNode.Parse((await agent.NextAsync("preview")).InputJson)!;
        Assert.Equal("octopushy-primary.pem", preview["env"]!["TECHNITIUM_CACERT"]!.GetValue<string>());
        Assert.Equal(Issue(PrimaryEndpoint).Pem, preview["files"]!["octopushy-primary.pem"]!.GetValue<string>());
        Assert.DoesNotContain("SKIP_TLS", preview["env"]!.ToJsonString());
    }

    [Fact]
    public async Task A_member_presenting_another_certificate_stops_the_work_until_it_is_trusted()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await FormAsync(server, agent);

        var cluster = await ClusterSummaryAsync(client);
        var id = cluster["id"]!.GetValue<Guid>();
        (await client.PostAsync($"/api/clusters/{id}/plan", null)).EnsureSuccessStatusCode();
        var changed = Certificate(SecondaryEndpoint, generation: 1);
        var inspection = Inspection("unverified");
        inspection["certificates"] = new JsonArray(changed);
        await agent.ReportAsync(await agent.NextAsync("inspect"), Outcome.Succeeded, inspection);
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("presents another certificate", run.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Secrets.ContainsKey(SecretNames.ClusterPassword));
        var trust = (await ClusterSummaryAsync(client))["trust"]!;
        var shown = trust["certificates"]!.AsArray().Single(c => c!["endpoint"]!.GetValue<string>() == SecondaryEndpoint)!;
        Assert.False(shown["trusted"]!.GetValue<bool>());
        Assert.Equal(Sha(SecondaryEndpoint, 1), shown["sha256"]!.GetValue<string>());

        // What was read is what can be trusted, and nothing else.
        var stale = await client.PostAsJsonAsync($"/api/clusters/{id}/trust", new
        {
            endpoints = trust["endpoints"],
            codeDigest = trust["codeDigest"]!.GetValue<string>(),
            certificates = new Dictionary<string, string> { [SecondaryEndpoint] = new string('d', 64) },
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        (await PostTrustAsync(client, await ClusterSummaryAsync(client))).EnsureSuccessStatusCode();
        await server.TickAsync();
        var next = await agent.NextAsync("preview");
        Assert.Equal(Issue(PrimaryEndpoint).Pem, JsonNode.Parse(next.InputJson)!["files"]!["octopushy-primary.pem"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_certificate_that_changes_during_a_check_is_kept_for_review_and_the_check_fails()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await FormAsync(server, agent);
        var id = (await ClusterSummaryAsync(client))["id"]!.GetValue<Guid>();
        (await client.PostAsync($"/api/clusters/{id}/plan", null)).EnsureSuccessStatusCode();
        await PlanAsync(agent, PlanOf());
        var changed = Certificate(SecondaryEndpoint, generation: 1);
        await agent.ReportAsync(await VerifyAsync(server, agent), Outcome.Failed,
            new JsonObject { ["ok"] = false, ["members"] = new JsonArray(), ["certificates"] = new JsonArray(changed) },
            "home/moraband/mrb-dns-01 (10.1.0.42): it presents another certificate than the one trusted.");
        await server.TickAsync();

        var cluster = await ClusterSummaryAsync(client);
        Assert.Equal("verification-failed", cluster["status"]!.GetValue<string>());
        Assert.Contains(cluster["trust"]!["certificates"]!.AsArray(),
            c => c!["sha256"]!.GetValue<string>() == Sha(SecondaryEndpoint, 1) && !c["trusted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_fingerprint_that_is_not_the_one_of_its_certificate_is_not_kept()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository, trust: false);
        await using var _ = server;
        var lying = Certificate(PrimaryEndpoint);
        lying["sha256"] = Sha(SecondaryEndpoint);
        var foreign = Certificate("https://10.1.0.99:53443");
        await ReadCertificatesAsync(server, agent, client, lying, foreign, Certificate(SecondaryEndpoint));

        var seen = (await ClusterSummaryAsync(client))["trust"]!["certificates"]!.AsArray();
        Assert.DoesNotContain(seen, c => c!["endpoint"]!.GetValue<string>() == PrimaryEndpoint && c["sha256"] is not null);
        Assert.DoesNotContain(seen, c => c!["endpoint"]!.GetValue<string>() == "https://10.1.0.99:53443");
        Assert.Contains(seen, c => c!["endpoint"]!.GetValue<string>() == SecondaryEndpoint && c["sha256"]?.GetValue<string>() == Sha(SecondaryEndpoint));
    }

    [Fact]
    public async Task A_pem_that_is_not_the_certificate_trusted_is_not_pinned_to_the_provider()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var __ = server;
        // A record from before the check, or written by other means: the fingerprint of one
        // certificate, the PEM of another, and the PEM is what the provider would trust.
        await server.WithDbAsync(async db =>
        {
            var trust = await db.ClusterTrusts.SingleAsync();
            var certificates = ClusterCertificates.Parse(trust.Certificates);
            certificates[PrimaryEndpoint] = new MemberCertificate(Sha(PrimaryEndpoint), Issue(PrimaryEndpoint, 1).Pem);
            trust.Certificates = ClusterCertificates.Serialize(certificates);
            await db.SaveChangesAsync();
        });
        await agent.ReportAsync(await agent.NextAsync("inspect"), Outcome.Succeeded, Inspection("absent"));
        await server.TickAsync();
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Contains("does not match its fingerprint", run.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Secrets.ContainsKey(SecretNames.ClusterPassword));
    }

    [Fact]
    public async Task A_commit_that_moves_a_member_waits_before_the_password_goes_there()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await agent.ReportAsync(await agent.NextAsync("inspect"), Outcome.Succeeded, Inspection("absent"));
        agent.TakeAssignments();
        await server.TickAsync();

        repository.Write(ClusterDefinitionTests.SecondaryFile, ClusterDefinitionTests.Guest("moraband", 420, "10.1.0.99"));
        server.Commit("Point the member somewhere else");
        await server.SyncAsync();
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("https://10.1.0.99:53443 is a new member endpoint", run.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor == "technitium" || a.InputJson.Contains("10.1.0.99") && a.Secrets.ContainsKey(SecretNames.ClusterPassword));

        // What the operator saw is what gets trusted.
        var shown = await ClusterSummaryAsync(client);
        shown["trust"]!["endpoints"] = new JsonArray("https://10.1.0.32:53443", "https://10.1.0.77:53443");
        Assert.Equal(HttpStatusCode.Conflict, (await PostTrustAsync(client, shown)).StatusCode);
        Assert.Equal(RunStatuses.Blocked, (await server.LatestRunAsync(Cluster, Operations.Cluster)).Status);

        await TrustAsync(server, agent, client);
        Assert.True((await ClusterSummaryAsync(client))["trust"]!["untrusted"]!.AsArray().Count == 0);
    }

    [Fact]
    public async Task Changed_recipe_code_waits_for_trust_because_it_runs_with_the_password()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf());
        await agent.ReportAsync(await VerifyAsync(server, agent), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();

        repository.Write("modules/opentofu/cluster/main.tf", "data \"external\" \"leak\" {}\n");
        server.Commit("Change the root");
        await server.SyncAsync();
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("recipe code", run.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Secrets.ContainsKey(SecretNames.ClusterPassword));
        Assert.True((await ClusterSummaryAsync(client))["trust"]!["codeChanged"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_cluster_run_waits_for_its_members_and_names_only_those()
    {
        using var repository = ClusterDefinitionTests.Repository();
        await using var server = await ServerHarness.StartAsync(postgres, repository);
        await server.SetSecretAsync("dns/admin", Password);
        var agent = await server.ConnectAgentAsync("home");
        using var client = await InstallationTrustTests.AuthenticateAsync(server);
        await server.SyncAndTrustAsync(client);
        await TrustAsync(server, agent, client);
        await server.TickAsync();

        var waiting = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.Blocked, waiting.Status);
        Assert.StartsWith("Waiting for member guests:", waiting.Detail);
        Assert.Contains("krb-dns-01", waiting.Detail);
        Assert.Contains("mrb-dns-01", waiting.Detail);

        // One member's failure holds back its cluster, as blocked work naming that member.
        await FinishGuestsAsync(server, agent, guests: 1, failing: "mrb-dns-01");
        await server.TickAsync();
        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("mrb-dns-01 is not applied from its current definition", run.Detail);
        Assert.DoesNotContain("krb-dns-01", run.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Executor == "technitium");
    }

    [Fact]
    public async Task A_first_run_forms_joins_refreshes_and_verifies_with_the_password_only_in_secrets()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var __ = server;

        var inspect = await agent.NextAsync("inspect");
        Assert.Equal("technitium", inspect.Executor);
        Assert.Equal(Password, inspect.Secrets[SecretNames.ClusterPassword]);
        var inspectInput = JsonNode.Parse(inspect.InputJson)!;
        Assert.False(inspectInput["formed"]!.GetValue<bool>());
        Assert.Equal("https://10.1.0.42:53443", inspectInput["members"]![1]!["endpoint"]!.GetValue<string>());
        Assert.Equal(new JsonArray("bad.example.com").ToJsonString(), inspectInput["blocked"]!.ToJsonString());
        await agent.ReportAsync(inspect, Outcome.Succeeded, Inspection("absent"));

        var preview = await agent.NextAsync("preview");
        var input = JsonNode.Parse(preview.InputJson)!;
        Assert.Equal("home.clusters.technitium", input["stack"]!.GetValue<string>());
        Assert.Equal("octopushy", input["backend"]!["type"]!.GetValue<string>());
        Assert.Equal("technitium.password", input["secretEnv"]!["TECHNITIUM_PASSWORD"]!.GetValue<string>());
        Assert.Equal(Password, preview.Secrets[SecretNames.ClusterPassword]);
        Assert.False(preview.Secrets.ContainsKey(SecretNames.ProxmoxToken));
        Assert.DoesNotContain(Password, preview.InputJson);
        Assert.Equal(Secondary(), input["replace"]![0]!.GetValue<string>());
        await agent.ReportAsync(preview, Outcome.Succeeded,
            PlanOf(Forming, (Secondary(), ["create"]), ("terraform_data.undeclared_entries", ["create"])), artifacts: FakeAgent.PlanArtifact());

        var apply = await agent.NextAsync("apply");
        Assert.Contains(apply.Artifacts, a => a.Name == "plan.tar.gz");
        await ApplyAsync(agent, apply);

        var reconcile = await agent.NextAsync("reconcile");
        var reconcileInput = JsonNode.Parse(reconcile.InputJson)!;
        Assert.Equal(ClusterDefinitionTests.Secondary, reconcileInput["join"]![0]!.GetValue<string>());
        Assert.Empty(reconcileInput["pruneBlocked"]!.AsArray());
        Assert.Empty(reconcileInput["pruneAllowed"]!.AsArray());
        await agent.ReportAsync(reconcile, Outcome.Succeeded, new JsonObject());

        await agent.ReportAsync(await agent.NextAsync("refresh-block-lists"), Outcome.Succeeded, new JsonObject { ["refreshed"] = true });
        await agent.ReportAsync(await agent.NextAsync("verify"), Outcome.Succeeded, new JsonObject { ["ok"] = true, ["members"] = new JsonArray() });
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.DoesNotContain(Password, run.Context);
        var resource = await server.QueryAsync(db => db.Resources.SingleAsync(r => r.Address == Cluster));
        Assert.Equal(ClusterDefinitionTests.Primary, resource.BoundPrimary);
        Assert.Equal("cluster.home.arpa", resource.BoundClusterDomain);
        Assert.Equal(resource.DesiredInputsHash, resource.SyncedInputsHash);
        Assert.NotNull(resource.VerifiedAt);
        Assert.Equal(1, await server.QueryAsync(db => db.TofuStates.CountAsync(s => s.ResourceId == resource.Id)));
    }

    [Fact]
    public async Task A_plan_with_no_changes_still_verifies_every_member()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf());

        var verify = await VerifyAsync(server, agent);
        Assert.Equal(2, JsonNode.Parse(verify.InputJson)!["members"]!.AsArray().Count);
        await agent.ReportAsync(verify, Outcome.Failed, new JsonObject { ["ok"] = false, ["members"] = new JsonArray() },
            "home/moraband/mrb-dns-01 (10.1.0.42): it reports clusterInitialized=false.");
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.StartsWith("Verification failed: home/moraband/mrb-dns-01", run.Detail);
        Assert.All(run.Steps.Where(s => s.Name is StepNames.DetachMembers or StepNames.Apply or StepNames.ReconcileCluster),
            s => Assert.Equal(StepStatuses.Skipped, s.Status));
        var cluster = await ClusterSummaryAsync(client);
        Assert.Equal("verification-failed", cluster["status"]!.GetValue<string>());
        // The member guests themselves stay as they were.
        var inventory = (await client.GetFromJsonAsync<JsonObject>("/api/inventory"))!;
        foreach (var installation in inventory["labs"]![0]!["installations"]!.AsArray())
            Assert.Equal("in-sync", installation!["guests"]![0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_member_out_of_the_cluster_is_in_the_preview_and_follows_approval()
    {
        using var repository = ClusterDefinitionTests.Repository(ClusterDefinitionTests.Definition + "\npolicy: { approval: manual }\n");
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        var preview = await PlanAsync(agent, PlanOf((Secondary(), ["delete", "create"])), Inspection("absent"));
        Assert.Equal(Secondary(), JsonNode.Parse(preview.InputJson)!["replace"]![0]!.GetValue<string>());
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/runs/{run.Id}"))!;
        var approval = detail["steps"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "approval")!;
        (await client.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval["id"]}/approve",
            new { planSha256 = approval["approval"]!["planSha256"]!.GetValue<string>() })).EnsureSuccessStatusCode();

        await ApplyAsync(agent, await agent.NextAsync("apply"));
        var reconcile = JsonNode.Parse((await agent.NextAsync("reconcile")).InputJson)!;
        Assert.Equal(ClusterDefinitionTests.Secondary, reconcile["join"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task A_stale_member_is_not_replaced_so_nothing_rejoins_it_behind_the_operators_back()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var __ = server;
        var preview = await PlanAsync(agent, PlanOf(), Inspection("stale"));
        Assert.Empty(JsonNode.Parse(preview.InputJson)!["replace"]!.AsArray());
        await VerifyAsync(server, agent);
    }

    [Fact]
    public async Task Undeclared_entries_are_replaced_in_the_plan_and_pruned_after_it()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var __ = server;
        var inspection = Inspection();
        inspection["undeclared"]!["blocked"] = new JsonArray("hand.example.com");
        var preview = await PlanAsync(agent, PlanOf(("terraform_data.undeclared_entries", ["delete", "create"])), inspection);
        Assert.Equal("terraform_data.undeclared_entries", JsonNode.Parse(preview.InputJson)!["replace"]![0]!.GetValue<string>());
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        var reconcile = await agent.NextAsync("reconcile");
        var removing = JsonNode.Parse(reconcile.InputJson)!;
        // Exactly what the approval named: anything that appeared since is not approved.
        Assert.Equal(["hand.example.com"], removing["pruneBlocked"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Empty(removing["pruneAllowed"]!.AsArray());
        await agent.ReportAsync(reconcile, Outcome.Succeeded, new JsonObject());
        await agent.ReportAsync(await VerifyAsync(server, agent), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();
        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(StepStatuses.Succeeded, run.Steps.Single(s => s.Name == StepNames.ReconcileCluster).Status);
    }

    [Fact]
    public async Task Removing_a_member_waits_under_the_destructive_policy_even_when_changes_are_automatic()
    {
        using var repository = ClusterDefinitionTests.Repository(ClusterDefinitionTests.Definition + "\npolicy: { approval: automatic, destructive: manual }\n");
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf((Secondary(), ["delete"])));
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains("destructive is manual", run.Detail);
        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/runs/{run.Id}"))!;
        var plan = detail["steps"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "plan")!["plan"]!;
        Assert.Equal(1, plan["counts"]!["delete"]!.GetValue<int>());
        Assert.Equal(Secondary(), plan["changes"]![0]!["address"]!.GetValue<string>());
    }

    /// <summary>Takes a plan through approval. It is the plan's own approval step, by its own digest.</summary>
    private static async Task ApproveAsync(HttpClient client, Run run)
    {
        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/runs/{run.Id}"))!;
        var approval = detail["steps"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "approval")!;
        (await client.PostAsJsonAsync($"/api/runs/{run.Id}/steps/{approval["id"]}/approve",
            new { planSha256 = approval["approval"]!["planSha256"]!.GetValue<string>() })).EnsureSuccessStatusCode();
    }

    /// <summary>A cluster whose secondary is about to be replaced, and the commit that replaces it.</summary>
    private static async Task ReplaceSecondaryAsync(ServerHarness server, TestRepository repository, FakeAgent agent, HttpClient client)
    {
        await FormAsync(server, agent);
        repository.Write("labs/home/proxmox/moraband/guests/mrb-dns-02/guest.yaml", ClusterDefinitionTests.Guest("moraband", 421, "10.1.0.43"));
        repository.Write(ClusterFile, ClusterDefinitionTests.Definition.Replace("moraband/mrb-dns-01", "moraband/mrb-dns-02"));
        server.Commit("Replace the secondary");
        await server.SyncAsync();
        await FinishGuestsAsync(server, agent, guests: 1);
        await ReadCertificatesAsync(server, agent, client, Certificate(PrimaryEndpoint), Certificate("https://10.1.0.43:53443"));
        (await PostTrustAsync(client, await ClusterSummaryAsync(client))).EnsureSuccessStatusCode();
        await server.TickAsync();
    }

    [Fact]
    public async Task A_removal_names_the_exact_peer_and_happens_before_the_apply_forgets_it()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await ReplaceSecondaryAsync(server, repository, agent, client);
        var inspect = await agent.NextAsync("inspect");
        await agent.ReportAsync(inspect, Outcome.Succeeded, Inspection("absent"));
        var preview = await agent.NextAsync("preview");
        await agent.ReportAsync(preview, Outcome.Succeeded,
            PlanOf((Secondary(), ["delete"]), (Secondary("home/moraband/mrb-dns-02"), ["create"])), artifacts: FakeAgent.PlanArtifact());

        // Detach comes first, or a failure here would leave nothing in the next plan to retry.
        var detach = await agent.NextAsync("detach");
        var remove = JsonNode.Parse(detach.InputJson)!["remove"]!.AsArray();
        Assert.Equal(ClusterDefinitionTests.Secondary, Assert.Single(remove)!["guest"]!.GetValue<string>());
        Assert.Equal("10.1.0.42", remove[0]!["address"]!.GetValue<string>());
        await agent.ReportAsync(detach, Outcome.Failed, new JsonObject { ["detached"] = new JsonArray() }, "the peer stayed");
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "apply");
        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Cluster, Operations.Cluster)).Status);

        // The delete is still in the next plan, so this is retried by ordinary means.
        var id = (await ClusterSummaryAsync(client))["id"]!.GetValue<Guid>();
        (await client.PostAsync($"/api/clusters/{id}/plan", null)).EnsureSuccessStatusCode();
        await agent.ReportAsync(await agent.NextAsync("inspect"), Outcome.Succeeded, Inspection("absent"));
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded,
            PlanOf((Secondary(), ["delete"]), (Secondary("home/moraband/mrb-dns-02"), ["create"])), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("detach"), Outcome.Succeeded, new JsonObject { ["detached"] = new JsonArray(ClusterDefinitionTests.Secondary) });
        var apply = await agent.NextAsync("apply");
        await ApplyAsync(agent, apply);
        await agent.NextAsync("reconcile");

        var binding = await server.QueryAsync(db => db.ClusterBindings.SingleAsync(b => b.Guest == ClusterDefinitionTests.Secondary));
        Assert.NotNull(binding.DetachedAt);
        Assert.StartsWith("run #", binding.DetachedBy);
    }

    [Fact]
    public async Task A_member_that_left_stays_recorded_when_another_removal_in_the_step_failed()
    {
        // Two removals, and one of them failed: what the other did has still happened.
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await ReplaceSecondaryAsync(server, repository, agent, client);
        await agent.ReportAsync(await agent.NextAsync("inspect"), Outcome.Succeeded, Inspection("absent"));
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded,
            PlanOf((Secondary(), ["delete"]), (Secondary("home/moraband/mrb-dns-02"), ["create"])), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("detach"), Outcome.Failed, new JsonObject
        {
            ["detached"] = new JsonArray(ClusterDefinitionTests.Secondary),
            ["unconfirmed"] = new JsonArray(),
        }, "another peer stayed");
        await server.TickAsync();

        var binding = await server.QueryAsync(db => db.ClusterBindings.SingleAsync(b => b.Guest == ClusterDefinitionTests.Secondary));
        Assert.NotNull(binding.DetachedAt);
        Assert.StartsWith("run #", binding.DetachedBy);
    }

    [Fact]
    public async Task A_member_that_joined_keeps_its_identity_when_another_join_in_the_step_failed()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var __ = server;
        await PlanAsync(agent, PlanOf(Forming, (Secondary(), ["create"])));
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        await agent.ReportAsync(await agent.NextAsync("reconcile"), Outcome.Failed, new JsonObject
        {
            ["joined"] = new JsonArray(ClusterDefinitionTests.Secondary),
            ["members"] = new JsonArray(new JsonObject { ["guest"] = ClusterDefinitionTests.Secondary, ["peerId"] = 22, ["peerName"] = "mrb-dns-01.cluster.home.arpa" }),
        }, "another member could not join");
        await server.TickAsync();

        var binding = await server.QueryAsync(db => db.ClusterBindings.SingleAsync(b => b.Guest == ClusterDefinitionTests.Secondary));
        Assert.Equal(22, binding.PeerId);
    }

    [Fact]
    public async Task A_member_that_joined_keeps_its_identity_when_the_outcome_of_another_join_is_unknown()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var __ = server;
        await PlanAsync(agent, PlanOf(Forming, (Secondary(), ["create"])));
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        await agent.ReportAsync(await agent.NextAsync("reconcile"), Outcome.Uncertain, new JsonObject
        {
            ["joined"] = new JsonArray(ClusterDefinitionTests.Secondary),
            ["members"] = new JsonArray(new JsonObject { ["guest"] = ClusterDefinitionTests.Secondary, ["peerId"] = 22, ["peerName"] = "mrb-dns-01.cluster.home.arpa" }),
        }, "the answer to joining another member was lost");
        await server.TickAsync();

        var binding = await server.QueryAsync(db => db.ClusterBindings.SingleAsync(b => b.Guest == ClusterDefinitionTests.Secondary));
        Assert.Equal(22, binding.PeerId);
        Assert.Equal("mrb-dns-01.cluster.home.arpa", binding.PeerName);
    }

    [Fact]
    public async Task A_guest_added_again_after_it_left_is_not_taken_for_the_peer_it_was()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var __ = server;
        await FormAsync(server, agent);
        await server.WithDbAsync(async db =>
        {
            var old = await db.ClusterBindings.SingleAsync(b => b.Guest == ClusterDefinitionTests.Secondary);
            old.PeerId = 22;
            old.PeerName = "mrb-dns-01.cluster.home.arpa";
            old.DetachedAt = DateTimeOffset.UtcNow;
            old.DetachedBy = "run #1";
            await db.SaveChangesAsync();
        });

        // The same address is declared again, and bound again by the next apply.
        await server.WithDbAsync(async db =>
        {
            var run = await db.Runs.OrderByDescending(r => r.CreatedAt).FirstAsync(r => r.Operation == Operations.Cluster);
            await ClusterPlan.BindMembersAsync(db, run, RunContext.Parse(run.Context).Cluster!, DateTimeOffset.UtcNow, default);
            await db.SaveChangesAsync();
        });

        var binding = await server.QueryAsync(db => db.ClusterBindings.SingleAsync(b => b.Guest == ClusterDefinitionTests.Secondary));
        Assert.Null(binding.DetachedAt);
        Assert.Null(binding.PeerId);
        Assert.Null(binding.PeerName);
    }

    [Fact]
    public async Task A_member_the_primary_only_forgot_stays_protected_until_an_operator_says_it_left()
    {
        // removeSecondary failed, so all that happened is that the primary's record was deleted.
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await ReplaceSecondaryAsync(server, repository, agent, client);
        repository.Delete("labs/home/proxmox/moraband/guests/mrb-dns-01");
        server.Commit("Delete the old guest");
        await server.SyncAsync();
        await server.TickAsync();

        await agent.ReportAsync(await agent.NextAsync("inspect"), Outcome.Succeeded, Inspection("absent"));
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded,
            PlanOf((Secondary(), ["delete"]), (Secondary("home/moraband/mrb-dns-02"), ["create"])), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("detach"), Outcome.Succeeded, new JsonObject
        {
            ["detached"] = new JsonArray(),
            ["unconfirmed"] = new JsonArray(ClusterDefinitionTests.Secondary),
        });
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        await agent.ReportAsync(await agent.NextAsync("reconcile"), Outcome.Succeeded, new JsonObject());
        await agent.ReportAsync(await VerifyAsync(server, agent), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();

        var retire = await server.LatestRunAsync(ClusterDefinitionTests.Secondary, Operations.Retire);
        Assert.Equal(RunStatuses.Blocked, retire.Status);
        Assert.Contains("nothing saw it leave", retire.Detail);
        var cluster = (await ClusterSummaryAsync(client))["id"]!.GetValue<Guid>();
        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/clusters/{cluster}"))!;
        var leaving = detail["leaving"]!.AsArray().Single(l => l!["guest"]!.GetValue<string>() == ClusterDefinitionTests.Secondary)!;
        Assert.True(leaving["unconfirmed"]!.GetValue<bool>());

        // Only an operator's word ends this, and the guest can then go.
        var guest = leaving["guestId"]!.GetValue<Guid>();
        (await client.PostAsJsonAsync($"/api/clusters/{cluster}/members/{guest}/detached", new { note = "rebuilt from nothing" })).EnsureSuccessStatusCode();
        await server.TickAsync();
        Assert.NotEqual(RunStatuses.Blocked, (await server.LatestRunAsync(ClusterDefinitionTests.Secondary, Operations.Retire)).Status);
    }

    [Fact]
    public async Task A_background_check_reads_certificates_first_and_releases_nothing_to_a_changed_one()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await FormAsync(server, agent);
        var planner = server.Services.GetRequiredService<Planner>();
        var released = await server.QueryAsync(db => db.SecretResolutions.CountAsync());

        // It is due and nothing has looked lately: the look comes first, and takes no secret.
        await CheckDueAsync(server, certificatesRead: false);
        await planner.ScheduleObservesAsync(CancellationToken.None);
        var read = await agent.NextKeepingOthersAsync("certificates");
        Assert.Empty(read.Secrets);
        var changed = Certificate(SecondaryEndpoint, generation: 1);
        await agent.ReportAsync(read, Outcome.Succeeded, new JsonObject { ["certificates"] = new JsonArray(Certificate(PrimaryEndpoint), changed) });
        await server.TickAsync();

        // It stays that way however often the check comes round.
        await planner.ScheduleObservesAsync(CancellationToken.None);
        await server.TickAsync();
        await planner.ScheduleObservesAsync(CancellationToken.None);
        await server.TickAsync();
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "verify");
        Assert.Equal(released, await server.QueryAsync(db => db.SecretResolutions.CountAsync()));

        var cluster = await ClusterSummaryAsync(client);
        Assert.Equal("verification-failed", cluster["status"]!.GetValue<string>());
        Assert.Contains("another certificate", cluster["statusDetail"]!.GetValue<string>());
        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/clusters/{cluster["id"]}"))!;
        Assert.False(detail["verification"]!["ok"]!.GetValue<bool>());
        Assert.DoesNotContain(detail["verification"]!["members"]!.AsArray(), m => m!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_background_check_of_unchanged_certificates_goes_on_to_the_check_itself()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var __ = server;
        await FormAsync(server, agent);
        var planner = server.Services.GetRequiredService<Planner>();
        await CheckDueAsync(server, certificatesRead: false);
        await planner.ScheduleObservesAsync(CancellationToken.None);
        var read = await agent.NextKeepingOthersAsync("certificates");
        await agent.ReportAsync(read, Outcome.Succeeded, new JsonObject { ["certificates"] = new JsonArray(Certificate(PrimaryEndpoint), Certificate(SecondaryEndpoint)) });
        await server.TickAsync();
        await planner.ScheduleObservesAsync(CancellationToken.None);
        Assert.Equal(Password, (await VerifyAsync(server, agent)).Secrets[SecretNames.ClusterPassword]);
    }

    [Fact]
    public async Task A_guest_can_be_retired_once_its_cluster_saw_it_leave()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await ReplaceSecondaryAsync(server, repository, agent, client);
        repository.Delete("labs/home/proxmox/moraband/guests/mrb-dns-01");
        server.Commit("Delete the old guest");
        await server.SyncAsync();
        await server.TickAsync();
        Assert.Equal(RunStatuses.Blocked, (await server.LatestRunAsync(ClusterDefinitionTests.Secondary, Operations.Retire)).Status);

        // The blocked removal does not hold up the cluster run that takes the guest out.
        await agent.ReportAsync(await agent.NextAsync("inspect"), Outcome.Succeeded, Inspection("absent"));
        await agent.ReportAsync(await agent.NextAsync("preview"), Outcome.Succeeded,
            PlanOf((Secondary(), ["delete"]), (Secondary("home/moraband/mrb-dns-02"), ["create"])), artifacts: FakeAgent.PlanArtifact());
        await agent.ReportAsync(await agent.NextAsync("detach"), Outcome.Succeeded, new JsonObject { ["detached"] = new JsonArray(ClusterDefinitionTests.Secondary) });
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        await agent.ReportAsync(await agent.NextAsync("reconcile"), Outcome.Succeeded, new JsonObject());
        await agent.ReportAsync(await VerifyAsync(server, agent), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();

        // It went on as soon as the guest had left: the plan of its removal has an agent.
        var retire = await server.LatestRunAsync(ClusterDefinitionTests.Secondary, Operations.Retire);
        Assert.NotEqual(RunStatuses.Blocked, retire.Status);
        Assert.NotNull(retire.StartedAt);
    }

    [Fact]
    public async Task A_guest_left_by_hand_is_acknowledged_by_an_operator_and_only_once_it_is_not_declared()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await FormAsync(server, agent);
        var cluster = (await ClusterSummaryAsync(client))["id"]!.GetValue<Guid>();
        var guest = GuestId(server, ClusterDefinitionTests.Secondary);
        var acknowledge = () => client.PostAsJsonAsync($"/api/clusters/{cluster}/members/{guest}/detached", new { note = "removed in the Technitium UI" });

        Assert.Equal(HttpStatusCode.Conflict, (await acknowledge()).StatusCode);

        repository.Delete("labs/home/clusters/technitium");
        repository.Delete("labs/home/proxmox/moraband/guests/mrb-dns-01");
        server.Commit("Delete the cluster and a member");
        await server.SyncAsync();
        await server.TickAsync();
        Assert.Equal(RunStatuses.Blocked, (await server.LatestRunAsync(ClusterDefinitionTests.Secondary, Operations.Retire)).Status);

        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/clusters/{cluster}"))!;
        // Sorted, and both are leaving: the whole cluster went.
        Assert.Equal([ClusterDefinitionTests.Primary, ClusterDefinitionTests.Secondary],
            detail["leaving"]!.AsArray().Select(l => l!["guest"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync($"/api/clusters/{cluster}/members/{guest}/detached", new { note = " " })).StatusCode);
        (await acknowledge()).EnsureSuccessStatusCode();
        await server.TickAsync();

        await agent.NextAsync("preview");
        var binding = await server.QueryAsync(db => db.ClusterBindings.SingleAsync(b => b.GuestId == guest));
        Assert.Equal("ops", binding.DetachedBy);
        Assert.Equal("removed in the Technitium UI", binding.DetachNote);
    }

    [Fact]
    public async Task A_queued_attempt_of_a_guest_waits_when_its_cluster_has_started()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var _ = server;
        await FormAsync(server, agent);
        var planner = server.Services.GetRequiredService<Planner>();
        var guestRun = await planner.PlanGuestAsync(GuestId(server, ClusterDefinitionTests.Secondary), CancellationToken.None);
        await server.TickAsync();
        Assert.NotEmpty(agent.TakeAssignments());

        // As after a restart: the guest's attempt is queued, and a started cluster run holds it.
        var clusterRun = await server.LatestRunAsync(Cluster, Operations.Cluster);
        await server.WithDbAsync(async db =>
        {
            await db.Attempts.Where(a => a.RunId == guestRun).ExecuteUpdateAsync(u => u
                .SetProperty(a => a.Status, AttemptStatuses.Queued).SetProperty(a => a.AgentId, (Guid?)null).SetProperty(a => a.AssignedAt, (DateTimeOffset?)null));
            await db.Runs.Where(r => r.Id == clusterRun.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RunStatuses.Running));
        });
        await server.Engine.DispatchAsync(CancellationToken.None);
        Assert.Empty(agent.TakeAssignments());
        var waiting = await server.QueryAsync(db => db.Attempts.Where(a => a.RunId == guestRun).Select(a => a.WaitingReason).FirstOrDefaultAsync());
        Assert.Contains("Waiting for cluster run", waiting);
        var blocked = await server.QueryAsync(db => db.Runs.FirstAsync(r => r.Id == guestRun));
        Assert.Equal(RunStatuses.Blocked, blocked.Status);
    }

    [Fact]
    public async Task A_primary_or_domain_change_on_a_formed_cluster_is_refused_before_planning()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf(Forming));
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        await agent.ReportAsync(await VerifyAsync(server, agent), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();

        repository.Write(ClusterFile, ClusterDefinitionTests.Definition
            .Replace("korriban/krb-dns-01, role: primary", "korriban/krb-dns-01, role: secondary")
            .Replace("moraband/mrb-dns-01, role: secondary", "moraband/mrb-dns-01, role: primary"));
        server.Commit("Promote");
        await server.SyncAsync();
        var cluster = await ClusterSummaryAsync(client);
        Assert.False(cluster["valid"]!.GetValue<bool>());
        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/clusters/{cluster["id"]}"))!;
        Assert.Contains(detail["issues"]!.AsArray(), i => i!["message"]!.GetValue<string>().Contains("promotion"));

        repository.Write(ClusterFile, ClusterDefinitionTests.Definition.Replace("clusterDomain: cluster.home.arpa", "clusterDomain: other.home.arpa"));
        server.Commit("Rename");
        await server.SyncAsync();
        detail = (await client.GetFromJsonAsync<JsonObject>($"/api/clusters/{cluster["id"]}"))!;
        Assert.Contains(detail["issues"]!.AsArray(), i => i!["message"]!.GetValue<string>().Contains("cannot change"));
        Assert.Equal(1, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Cluster)));
    }

    [Fact]
    public async Task Deleting_the_definition_reports_it_missing_and_destroys_nothing()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf());
        await agent.ReportAsync(await VerifyAsync(server, agent), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();

        repository.Delete("labs/home/clusters/technitium");
        server.Commit("Remove the cluster");
        await server.SyncAsync();
        await server.TickAsync();

        var resource = await server.QueryAsync(db => db.Resources.SingleAsync(r => r.Address == Cluster));
        Assert.NotNull(resource.MissingSince);
        Assert.Equal(1, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Cluster)));
        Assert.Equal(0, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Retire)));
        var cluster = await ClusterSummaryAsync(client);
        Assert.Equal("missing", cluster["status"]!.GetValue<string>());
        Assert.Contains("nothing was removed", cluster["statusDetail"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/clusters/{resource.Id}/plan", null)).StatusCode);

        // Restoring it picks the cluster up again.
        repository.Write(ClusterFile, ClusterDefinitionTests.Definition);
        server.Commit("Restore the cluster");
        await server.SyncAsync();
        Assert.Null((await server.QueryAsync(db => db.Resources.SingleAsync(r => r.Address == Cluster))).MissingSince);
    }

    [Fact]
    public async Task A_guest_listed_in_a_cluster_is_not_removed_when_its_folder_goes()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, _, client) = await ReadyAsync(repository);
        await using var __ = server;

        repository.Delete("labs/home/proxmox/moraband/guests/mrb-dns-01");
        server.Commit("Delete a member's folder");
        await server.SyncAsync();

        Assert.Equal(0, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Retire)));
        var guest = await server.QueryAsync(db => db.Resources.SingleAsync(r => r.Address == ClusterDefinitionTests.Secondary));
        Assert.Contains("still listed in home/clusters/technitium", guest.Issues);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/guests/{guest.Id}/plan", null)).StatusCode);
    }

    [Fact]
    public async Task A_member_guest_that_applied_a_change_gets_its_cluster_checked()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var __ = server;
        await PlanAsync(agent, PlanOf(Forming));
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        await agent.ReportAsync(await VerifyAsync(server, agent), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();

        var planner = server.Services.GetRequiredService<Planner>();
        await planner.ScheduleClusterChecksAsync(CancellationToken.None);
        Assert.Equal(1, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Cluster)));

        // A guest's plan that changes something, as replacing a container does.
        var guestId = await server.QueryAsync(db => db.Resources.Where(r => r.Address == ClusterDefinitionTests.Secondary).Select(r => r.Id).SingleAsync());
        await planner.PlanGuestAsync(guestId, CancellationToken.None);
        await FinishGuestsAsync(server, agent, guests: 1);
        await planner.ScheduleClusterChecksAsync(CancellationToken.None);
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal($"member {ClusterDefinitionTests.Secondary} changed", run.Reason);
        await PlanAsync(agent, PlanOf());
        await VerifyAsync(server, agent);

        // And once: one change does not keep proposing runs.
        await planner.ScheduleClusterChecksAsync(CancellationToken.None);
        Assert.Equal(2, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Cluster)));
    }

    [Fact]
    public async Task A_formed_cluster_is_observed_in_the_background_and_a_failure_surfaces()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf(Forming));
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        await agent.ReportAsync(await VerifyAsync(server, agent), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();

        await CheckDueAsync(server);
        await server.Services.GetRequiredService<Planner>().ScheduleObservesAsync(CancellationToken.None);
        await server.TickAsync();
        var check = await VerifyAsync(server, agent);
        Assert.Equal(Password, check.Secrets[SecretNames.ClusterPassword]);
        await agent.ReportAsync(check, Outcome.Failed, new JsonObject { ["ok"] = false }, "home/moraband/mrb-dns-01 (10.1.0.42): it reports clusterInitialized=false.");
        await server.TickAsync();

        var overview = (await client.GetFromJsonAsync<JsonObject>("/api/overview"))!;
        var error = overview["observeErrors"]!.AsArray().Single(e => e!["kind"]!.GetValue<string>() == "cluster")!;
        Assert.Equal(Cluster, error["address"]!.GetValue<string>());
        Assert.Contains("mrb-dns-01", error["error"]!.GetValue<string>());
        Assert.Equal(1, await server.QueryAsync(db => db.Runs.CountAsync(r => r.Operation == Operations.Cluster)));
    }

    [Fact]
    public async Task A_background_check_does_not_start_while_a_cluster_run_is_under_way()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await FormAsync(server, agent);
        var id = (await ClusterSummaryAsync(client))["id"]!.GetValue<Guid>();
        (await client.PostAsync($"/api/clusters/{id}/plan", null)).EnsureSuccessStatusCode();
        Assert.False(RunStatuses.IsTerminal((await server.LatestRunAsync(Cluster, Operations.Cluster)).Status));
        var before = await server.QueryAsync(db => db.Attempts.CountAsync(a => a.StepId == null && a.ResourceId == id));

        await CheckDueAsync(server);
        await server.Services.GetRequiredService<Planner>().ScheduleObservesAsync(CancellationToken.None);
        await server.TickAsync();

        Assert.Equal(before, await server.QueryAsync(db => db.Attempts.CountAsync(a => a.StepId == null && a.ResourceId == id)));
    }

    [Fact]
    public async Task A_check_that_finishes_after_a_newer_run_does_not_replace_what_the_run_found()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await FormAsync(server, agent);
        await CheckDueAsync(server);
        await server.Services.GetRequiredService<Planner>().ScheduleObservesAsync(CancellationToken.None);
        await server.TickAsync();
        var slow = await VerifyAsync(server, agent);

        // A run starts and ends clean while that check is still out.
        var id = (await ClusterSummaryAsync(client))["id"]!.GetValue<Guid>();
        (await client.PostAsync($"/api/clusters/{id}/plan", null)).EnsureSuccessStatusCode();
        await PlanAsync(agent, PlanOf());
        await agent.ReportAsync(await VerifyAsync(server, agent), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Cluster, Operations.Cluster)).Status);

        await agent.ReportAsync(slow, Outcome.Failed, new JsonObject { ["ok"] = false }, "home/moraband/mrb-dns-01 (10.1.0.42): it reported what it did before the run.");
        await server.TickAsync();

        var overview = (await client.GetFromJsonAsync<JsonObject>("/api/overview"))!;
        Assert.DoesNotContain(overview["observeErrors"]!.AsArray(), e => e!["kind"]!.GetValue<string>() == "cluster");
    }

    [Fact]
    public async Task Planning_by_hand_verifies_again_and_the_detail_shows_members()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf());
        await agent.ReportAsync(await VerifyAsync(server, agent), Outcome.Succeeded, new JsonObject
        {
            ["ok"] = true,
            ["members"] = new JsonArray(new JsonObject { ["guest"] = ClusterDefinitionTests.Primary, ["ok"] = true }),
        });
        await server.TickAsync();

        var id = (await ClusterSummaryAsync(client))["id"]!.GetValue<Guid>();
        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/clusters/{id}"))!;
        Assert.Equal(ClusterDefinitionTests.Primary, detail["cluster"]!["members"]![0]!["guest"]!.GetValue<string>());
        Assert.Equal("10.1.0.32", detail["cluster"]!["members"]![0]!["address"]!.GetValue<string>());
        Assert.Equal("in-sync", detail["cluster"]!["members"]![0]!["guestStatus"]!.GetValue<string>());
        Assert.True(detail["verification"]!["ok"]!.GetValue<bool>());
        Assert.Equal("manual", (await client.PostAsync($"/api/clusters/{id}/plan", null)).IsSuccessStatusCode ? "manual" : "refused");
        await server.TickAsync();
        Assert.Equal("manual", (await server.LatestRunAsync(Cluster, Operations.Cluster)).Reason);
    }

    /// <summary>Forms the cluster and leaves its first run finished.</summary>
    private static async Task FormAsync(ServerHarness server, FakeAgent agent)
    {
        await PlanAsync(agent, PlanOf(Forming, (Secondary(), ["create"])), Inspection("absent"));
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        await agent.ReportAsync(await agent.NextAsync("reconcile"), Outcome.Succeeded, new JsonObject());
        await agent.ReportAsync(await VerifyAsync(server, agent), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();
    }

    /// <summary>Makes the cluster's background check due, as time passing would.</summary>
    private static Task CheckDueAsync(ServerHarness server, bool certificatesRead = true) => server.WithDbAsync(async db =>
    {
        foreach (var resource in await db.Resources.Where(r => r.Kind == ResourceKinds.Cluster).ToListAsync())
        {
            resource.ObserveRequestedAt = DateTimeOffset.UtcNow.AddDays(-1);
            resource.CertificatesReadAt = certificatesRead ? DateTimeOffset.UtcNow : null;
        }

        await db.SaveChangesAsync();
    });

    private static Guid GuestId(ServerHarness server, string address) =>
        server.QueryAsync(db => db.Resources.Where(r => r.Address == address).Select(r => r.Id).SingleAsync()).GetAwaiter().GetResult();

    [Fact]
    public async Task A_member_cannot_start_mutating_while_its_cluster_is_applying()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf(Forming));
        var apply = await agent.NextAsync("apply");

        var planner = server.Services.GetRequiredService<Planner>();
        await planner.PlanGuestAsync(GuestId(server, ClusterDefinitionTests.Secondary), CancellationToken.None);
        await server.TickAsync();
        await server.TickAsync();

        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action is "preview" or "apply");
        var run = await server.LatestRunAsync(ClusterDefinitionTests.Secondary, Operations.Provision);
        Assert.Equal(RunStatuses.Blocked, run.Status);
        Assert.Contains("Waiting for cluster run", run.Detail);

        // An uncertain cluster apply keeps its members locked until someone looks at it.
        await agent.ReportAsync(apply, Outcome.Uncertain, error: "the agent restarted", artifacts: State());
        await server.TickAsync();
        Assert.Equal(RunStatuses.Uncertain, (await server.LatestRunAsync(Cluster, Operations.Cluster)).Status);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action is "preview" or "apply");
        Assert.Equal(RunStatuses.Blocked, (await server.LatestRunAsync(ClusterDefinitionTests.Secondary, Operations.Provision)).Status);

        await server.Engine.CloseUncertainAsync((await server.LatestRunAsync(Cluster, Operations.Cluster)).Id, "ops", CancellationToken.None);
        await agent.NextAsync("preview");
    }

    [Fact]
    public async Task Removing_undeclared_entries_waits_under_the_destructive_policy()
    {
        using var repository = ClusterDefinitionTests.Repository(ClusterDefinitionTests.Definition + "\npolicy: { approval: automatic, destructive: manual }\n");
        var (server, agent, _) = await ReadyAsync(repository);
        await using var _ = server;
        var inspection = Inspection();
        inspection["undeclared"]!["blocked"] = new JsonArray("hand.example.com");
        await PlanAsync(agent, PlanOf(("terraform_data.undeclared_entries", ["delete", "create"])), inspection);
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.Equal(RunStatuses.AwaitingApproval, run.Status);
        Assert.Contains("destructive is manual", run.Detail);
        Assert.Contains("hand.example.com", run.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action == "apply");
    }

    [Fact]
    public async Task A_first_run_has_nothing_to_prune_and_is_not_held_for_destructive_approval()
    {
        using var repository = ClusterDefinitionTests.Repository(ClusterDefinitionTests.Definition + "\npolicy: { approval: automatic, destructive: manual }\n");
        var (server, agent, _) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf(Forming, (Secondary(), ["create"]), ("terraform_data.undeclared_entries", ["create"])), Inspection("absent"));
        await server.TickAsync();

        var run = await server.LatestRunAsync(Cluster, Operations.Cluster);
        Assert.NotEqual(RunStatuses.AwaitingApproval, run.Status);
        Assert.DoesNotContain("nobody declared", run.Detail ?? "");
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        var reconcile = JsonNode.Parse((await agent.NextAsync("reconcile")).InputJson)!;
        Assert.Empty(reconcile["pruneBlocked"]!.AsArray());
    }

    [Theory]
    [InlineData("create", false, false)]
    [InlineData("create", true, true)]
    [InlineData("delete,create", false, true)]
    [InlineData("delete", false, true)]
    [InlineData("update", false, false)]
    public void Pruning_is_what_the_plan_does_to_the_entries_and_what_inspection_named(string actions, bool named, bool expected)
    {
        var plan = PlanOf(("terraform_data.undeclared_entries", actions.Split(',')));
        var inspection = Inspection();
        if (named)
            inspection["undeclared"]!["allowed"] = new JsonArray("hand.example.com");
        Assert.Equal(expected, ClusterPlan.PrunesEntries(plan, inspection));
        Assert.False(ClusterPlan.PrunesEntries(PlanOf((Secondary(), ["delete"])), inspection));
    }

    [Fact]
    public async Task A_guest_taken_out_of_the_cluster_cannot_be_destroyed_until_it_has_left()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var _ = server;
        await FormAsync(server, agent);

        // One commit replaces the secondary and deletes the old guest's folder.
        repository.Write("labs/home/proxmox/moraband/guests/mrb-dns-02/guest.yaml", ClusterDefinitionTests.Guest("moraband", 421, "10.1.0.43"));
        repository.Write(ClusterFile, ClusterDefinitionTests.Definition.Replace("moraband/mrb-dns-01", "moraband/mrb-dns-02"));
        repository.Delete("labs/home/proxmox/moraband/guests/mrb-dns-01");
        server.Commit("Replace the secondary");
        await server.SyncAsync();
        await server.TickAsync();
        await server.TickAsync();

        var retire = await server.LatestRunAsync(ClusterDefinitionTests.Secondary, Operations.Retire);
        Assert.Equal(RunStatuses.Blocked, retire.Status);
        Assert.Contains("still joined to home/clusters/technitium", retire.Detail);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action is "set-protection" or "apply");
    }

    [Fact]
    public async Task Deleting_the_cluster_keeps_its_guests_from_being_destroyed()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, _) = await ReadyAsync(repository);
        await using var _ = server;
        await FormAsync(server, agent);

        repository.Delete("labs/home/clusters/technitium");
        repository.Delete("labs/home/proxmox/moraband/guests/mrb-dns-01");
        server.Commit("Delete the cluster and a member");
        await server.SyncAsync();
        await server.TickAsync();
        await server.TickAsync();

        var retire = await server.LatestRunAsync(ClusterDefinitionTests.Secondary, Operations.Retire);
        Assert.Equal(RunStatuses.Blocked, retire.Status);
        Assert.DoesNotContain(agent.TakeAssignments(), a => a.Action is "set-protection" or "apply");
    }

    [Fact]
    public async Task A_failed_list_refresh_is_retried_by_a_run_with_no_changes()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf(Forming, ("terraform_data.undeclared_entries", ["create"])));
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        await agent.ReportAsync(await agent.NextAsync("refresh-block-lists"), Outcome.Failed, error: "the list server is down");
        await server.TickAsync();
        Assert.Equal(RunStatuses.Failed, (await server.LatestRunAsync(Cluster, Operations.Cluster)).Status);

        var id = (await ClusterSummaryAsync(client))["id"]!.GetValue<Guid>();
        (await client.PostAsync($"/api/clusters/{id}/plan", null)).EnsureSuccessStatusCode();
        await PlanAsync(agent, PlanOf());
        await agent.ReportAsync(await agent.NextAsync("refresh-block-lists"), Outcome.Succeeded, new JsonObject { ["refreshed"] = true });
        await agent.ReportAsync(await agent.NextAsync("verify"), Outcome.Succeeded, new JsonObject { ["ok"] = true });
        await server.TickAsync();
        Assert.Equal(RunStatuses.Succeeded, (await server.LatestRunAsync(Cluster, Operations.Cluster)).Status);

        // Done once: the next no-change run has nothing to retry.
        (await client.PostAsync($"/api/clusters/{id}/plan", null)).EnsureSuccessStatusCode();
        await PlanAsync(agent, PlanOf());
        await agent.NextAsync("verify");
        Assert.Equal(StepStatuses.Skipped, (await server.LatestRunAsync(Cluster, Operations.Cluster)).Steps.Single(s => s.Name == StepNames.RefreshBlockLists).Status);
    }

    [Fact]
    public async Task A_failed_background_check_replaces_older_member_results()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf(Forming));
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        await agent.ReportAsync(await VerifyAsync(server, agent), Outcome.Succeeded, new JsonObject
        {
            ["ok"] = true,
            ["members"] = new JsonArray(
                new JsonObject { ["guest"] = ClusterDefinitionTests.Primary, ["ok"] = true },
                new JsonObject { ["guest"] = ClusterDefinitionTests.Secondary, ["ok"] = true }),
        });
        await server.TickAsync();

        var planner = server.Services.GetRequiredService<Planner>();
        await CheckDueAsync(server);
        await planner.ScheduleObservesAsync(CancellationToken.None);
        var healthy = await VerifyAsync(server, agent);
        await agent.ReportAsync(healthy, Outcome.Succeeded, new JsonObject
        {
            ["ok"] = true,
            ["members"] = new JsonArray(new JsonObject { ["guest"] = ClusterDefinitionTests.Secondary, ["ok"] = true }),
        });
        await CheckDueAsync(server);
        await planner.ScheduleObservesAsync(CancellationToken.None);
        var failing = await VerifyAsync(server, agent);
        await agent.ReportAsync(failing, Outcome.Failed, new JsonObject
        {
            ["ok"] = false,
            ["members"] = new JsonArray(new JsonObject { ["guest"] = ClusterDefinitionTests.Secondary, ["ok"] = false }),
        }, "home/moraband/mrb-dns-01 (10.1.0.42): it reports clusterInitialized=false.");
        await server.TickAsync();

        var cluster = await ClusterSummaryAsync(client);
        Assert.Equal("verification-failed", cluster["status"]!.GetValue<string>());
        Assert.Contains("mrb-dns-01", cluster["statusDetail"]!.GetValue<string>());
        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/clusters/{cluster["id"]}"))!;
        Assert.False(detail["verification"]!["ok"]!.GetValue<bool>());
        Assert.False(detail["verification"]!["members"]![0]!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_background_check_that_returned_nothing_leaves_no_member_passed()
    {
        using var repository = ClusterDefinitionTests.Repository();
        var (server, agent, client) = await ReadyAsync(repository);
        await using var _ = server;
        await PlanAsync(agent, PlanOf(Forming));
        await ApplyAsync(agent, await agent.NextAsync("apply"));
        await agent.ReportAsync(await VerifyAsync(server, agent), Outcome.Succeeded, new JsonObject
        {
            ["ok"] = true,
            ["members"] = new JsonArray(new JsonObject { ["guest"] = ClusterDefinitionTests.Secondary, ["ok"] = true }),
        });
        await server.TickAsync();

        await CheckDueAsync(server);
        await server.Services.GetRequiredService<Planner>().ScheduleObservesAsync(CancellationToken.None);
        await agent.ReportAsync(await VerifyAsync(server, agent), Outcome.Failed, error: "the agent lost its route");
        await server.TickAsync();

        var cluster = await ClusterSummaryAsync(client);
        Assert.Equal("verification-failed", cluster["status"]!.GetValue<string>());
        var detail = (await client.GetFromJsonAsync<JsonObject>($"/api/clusters/{cluster["id"]}"))!;
        Assert.False(detail["verification"]!["ok"]!.GetValue<bool>());
        Assert.Empty(detail["verification"]!["members"]!.AsArray());

        // A newer good check settles it, whoever ran it.
        await CheckDueAsync(server);
        await server.Services.GetRequiredService<Planner>().ScheduleObservesAsync(CancellationToken.None);
        await agent.ReportAsync(await VerifyAsync(server, agent), Outcome.Succeeded, new JsonObject { ["ok"] = true, ["members"] = new JsonArray() });
        await server.TickAsync();
        Assert.Equal("in-sync", (await ClusterSummaryAsync(client))["status"]!.GetValue<string>());
    }
}
