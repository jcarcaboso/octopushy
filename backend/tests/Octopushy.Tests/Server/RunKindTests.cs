using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;
using Octopushy.Tests.Definitions;

namespace Octopushy.Tests.Server;

/// <summary>
/// A run's kind is stored in its context by the factory. A context stored before that gets the kind the engine inferred from
/// it then, in <see cref="RunContext.Parse"/> alone; one stored since keeps its own.
/// </summary>
public class RunKindTests
{
    private const string Guest = ServiceDefinitionTests.Guest;

    /// <summary>The kind the engine inferred, before it was stored, read from the same JSON without it.</summary>
    private static string Inferred(string json)
    {
        var old = JsonNode.Parse(json)!.AsObject();
        old.Remove("kind");
        return RunContext.Parse(old.ToJsonString()).Kind!;
    }

    private static string Stored(string json) => JsonNode.Parse(json)!["kind"]!.GetValue<string>();

    [Fact]
    public void Every_run_the_factory_makes_stores_the_kind_the_engine_inferred_for_it()
    {
        using var test = ServiceDefinitionTests.Repository();
        // The same guest, declared on a machine someone else made, for the attach run.
        using var attached = ServiceDefinitionTests.Repository();
        var file = Path.Combine(attached.Root, ServiceDefinitionTests.GuestFile);
        attached.Write(ServiceDefinitionTests.GuestFile, File.ReadAllText(file).Replace("type: vm\n", "type: vm\nmachine: attached\n"));
        var repository = LabRepositoryLoader.Load(test.Root);
        var attaching = LabRepositoryLoader.Load(attached.Root);
        Assert.True(repository.IsValid(Guest), string.Join("\n", repository.IssuesFor(Guest).Select(i => i.Message)));
        var factory = new RunFactory(Options.Create(new OctopushyOptions()), TimeProvider.System);
        var guest = repository.Guest(Guest)!;
        var resource = new Resource { Id = Guid.NewGuid(), Address = Guest, Kind = ResourceKinds.Guest, Lab = "home" };
        var service = new Resource { Id = Guid.NewGuid(), Address = ServiceDefinitionTests.Address, Kind = ResourceKinds.Service, Lab = "home" };
        var revision = new DefinitionRevision { Id = Guid.NewGuid(), Commit = new string('a', 40) };

        var runs = new Dictionary<string, (Run Run, string Kind)>
        {
            ["provision"] = (factory.Guest(repository, guest, resource, revision, "manual", keyIssued: true), RunKinds.Provision),
            ["retire"] = (factory.RemoveDeleted(repository, guest, resource, revision, new string('b', 40), "manual", keyIssued: true), RunKinds.Provision),
            ["attach"] = (factory.Attach(attaching, attaching.Guest(Guest)!, resource, revision, "ops"), RunKinds.Provision),
            ["configure"] = (factory.Configure(repository, guest, resource, revision, "setup", "manual", null, null), RunKinds.Configure),
            ["rotate-access"] = (factory.RotateAccess(repository, guest, resource, revision, new AccessRotationContext(1, 2, [1])), RunKinds.Configure),
            ["pipeline"] = (factory.Pipeline(repository, repository.Service(ServiceDefinitionTests.Address)!, service, resource, revision, "manual"),
                RunKinds.Pipeline),
        };
        Assert.Equal(
            [Operations.Attach, Operations.Configure, Operations.Pipeline, Operations.Provision, Operations.Retire, Operations.RotateAccess],
            runs.Values.Select(r => r.Run.Operation).Order(StringComparer.Ordinal));
        foreach (var (name, (run, kind)) in runs)
        {
            Assert.True(kind == Stored(run.Context), name);
            Assert.True(kind == Inferred(run.Context), name);
            Assert.True(kind == RunContext.Parse(run.Context).Kind, name);
        }

        // An observation is no run: its context is stored without a kind, and the provision kind it reads as is never asked.
        var observation = RunFactory.Observation(repository, repository.Installation(guest.InstallationAddress)!, revision).Serialize();
        Assert.Null(JsonNode.Parse(observation)!["kind"]);
        Assert.Equal(RunKinds.Provision, RunContext.Parse(observation).Kind);
    }

    private const string PipelineRow =
            """
            {"address":"home/korriban/krb-app-01/web","proxmox":{"endpoint":"https://10.1.0.10:8006/","verifyTls":false},
            "secrets":{"deploy/ansible.ssh-key":"k"},
            "stages":[{"name":"dns","workflow":"app@1.0","openTofu":{"stack":"s","root":"dns","image":"opentofu:1","variables":{},"env":{},"secretEnv":{}}},
            {"name":"deploy","workflow":"app@1.0","ansible":{"workflow":"app@1.0","action":"deploy","root":"r","playbook":"p","usesAppliedSnapshot":false,
            "destructive":false,"image":"i","guestName":"g","guestDirectory":"d","address":"10.1.0.31","port":22,"user":"ops","vars":{},"secretVars":[],
            "secretFiles":[],"outcomes":{}}}]}
            """;

    /// <summary>Contexts as runs stored them before the kind, in each shape the factory made, and the kind each is read as.</summary>
    public static TheoryData<string, string> OldRows() => new()
    {
        // A provision or retire run: OpenTofu and the Proxmox API.
        {
            """
            {"address":"home/korriban/krb-app-01","commit":"aaaa","agentLabels":[],"agentLabelsSetBy":"default","hostKey":"home/korriban","hostLimit":1,
            "policy":{"approval":"manual","approvalSetBy":"default","destructive":"manual","destructiveSetBy":"default","destructiveExplicit":false},
            "openTofu":{"stack":"home.korriban.krb-app-01","root":"tofu","image":"opentofu:1","variables":{},"env":{"PROXMOX_VE_ENDPOINT":"https://10.1.0.10:8006/"},
            "secretEnv":{"PROXMOX_VE_API_TOKEN":"proxmox.token"}},
            "proxmox":{"endpoint":"https://10.1.0.10:8006/","verifyTls":false,"node":"korriban","vmid":310,"name":"krb-app-01"},
            "secrets":{"proxmox.shared-token":"proxmox/home/token"},"requestedSecretVersions":{},"secretVersions":{},"secretBindingsPinned":false,"verify":"proxmox",
            "managedAccess":false,"containerHostKey":false,"managedHostKey":false}
            """,
            RunKinds.Provision
        },
        // An attach run, and the recovery tests' synthetic run: the Proxmox API alone.
        {
            """
            {"address":"fixture.host.guest","proxmox":{"endpoint":"https://proxmox.invalid","verifyTls":true,"node":"host","vmid":999,"name":"fixture"},
            "secrets":{"token":"t"},"secretVersions":{"token":1},"secretBindingsPinned":true}
            """,
            RunKinds.Provision
        },
        // A configure or rotate-access run: Ansible.
        {
            """
            {"address":"home/korriban/krb-app-01","proxmox":{"endpoint":"https://10.1.0.10:8006/","verifyTls":false,"node":"korriban","vmid":310,"name":"krb-app-01"},
            "secrets":{"ansible.ssh-key":"k"},"managedHostKey":true,
            "ansible":{"workflow":"app@1.0","action":"setup","root":"r","playbook":"p","usesAppliedSnapshot":false,"destructive":false,"image":"i",
            "guestName":"g","guestDirectory":"d","address":"10.1.0.31","port":22,"user":"ops","vars":{},"secretVars":[],"secretFiles":[],"outcomes":{}}}
            """,
            RunKinds.Configure
        },
        // A pipeline run: stages, each with one tool, and none at the top.
        { PipelineRow,
            RunKinds.Pipeline
        },
        // Stages win over Ansible, as they did.
        {
            """
            {"address":"a","proxmox":{"endpoint":"e","verifyTls":true},"stages":[],
            "ansible":{"workflow":"w","action":"a","root":"r","playbook":"p","usesAppliedSnapshot":false,"destructive":false,"image":"i","guestName":"g",
            "guestDirectory":"d","address":"x","port":22,"user":"u","vars":{},"secretVars":[],"secretFiles":[],"outcomes":{}}}
            """,
            RunKinds.Pipeline
        },
        // Nulls read as absent.
        {
            """{"address":"a","proxmox":{"endpoint":"e","verifyTls":true},"kind":null,"stages":null,"ansible":null,"openTofu":null}""",
            RunKinds.Provision
        },
    };

    [Theory]
    [MemberData(nameof(OldRows))]
    public void A_context_stored_before_the_kind_reads_as_the_kind_the_engine_inferred(string json, string kind)
    {
        var context = RunContext.Parse(json);
        Assert.Equal(kind, context.Kind);
        // Written back, as a pin of its bindings writes it, it keeps that kind.
        Assert.Equal(kind, Stored(context.Serialize()));
    }

    [Fact]
    public void A_stored_kind_is_read_as_it_was_stored_and_a_stage_sees_its_runs()
    {
        // Never inferred again once stored: a configure kind with no Ansible stays one.
        Assert.Equal(RunKinds.Configure, RunContext.Parse("""{"address":"a","kind":"configure","proxmox":{"endpoint":"e","verifyTls":true}}""").Kind);
        var pipeline = RunContext.Parse(PipelineRow);
        Assert.Equal(RunKinds.Pipeline, pipeline.ForStage("deploy").Kind);
        Assert.Equal(RunKinds.Pipeline, pipeline.ForStage("dns").Kind);
        // Still refused as before: a document that is not a context.
        Assert.ThrowsAny<Exception>(() => RunContext.Parse("[]"));
        Assert.Throws<InvalidOperationException>(() => RunContext.Parse("null"));
    }

    [Fact]
    public void The_kinds_are_constants()
    {
        var kinds = typeof(RunKinds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).Order(StringComparer.Ordinal);
        Assert.Equal(["configure", "pipeline", "provision"], kinds);
    }

    [Fact]
    public void The_registry_has_one_entry_for_each_step_name()
    {
        var names = typeof(StepNames).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).Order(StringComparer.Ordinal);
        Assert.Equal(names, StepTypes.All.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(StepTypes.All, t => Assert.Same(t, StepTypes.Find(t.Name)));
    }
}
