using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

/// <summary>
/// Interfaces: a binding in <c>labs/&lt;lab&gt;/interfaces/&lt;slot&gt;/&lt;name&gt;.yaml</c>, named by an installation, fills the slot a
/// workflow stage declares, with no knowledge of what the slot is. See docs/workflows.md#interfaces.
/// </summary>
public class InterfaceTests
{
    private const string Installation = "labs/home/proxmox/korriban/installation.yaml";
    private const string Binding = "labs/home/interfaces/dns/house.yaml";
    private const string Address = ServiceDefinitionTests.Address;

    private const string BindingYaml = """
        schemaVersion: 1
        description: The house's DNS servers
        inputs:
          primary: { name: dns-01, address: 10.1.0.53 }
        secrets:
          dns_token: { store: dns/house/token }
        """;

    private const string Service = """
        schemaVersion: 1
        workflow: web-dns@1
        inputs: { port: 9000, records: [{ name: web.example.com, type: CNAME, value: proxy.example.com }] }
        secrets:
          web_password: { store: web/password }
        """;

    /// <summary>The service repository with a workflow whose second stage fills <c>dns</c>, the slot's schema, and a binding.</summary>
    internal static TestRepository Repository(string service = Service, string binding = BindingYaml, bool bound = true)
    {
        var test = ServiceDefinitionTests.Repository(service);
        test.Write("workflows/web-dns/workflow.yaml", """
            schemaVersion: 1
            version: "1.0"
            manages: service
            secrets: [web_password]
            stages:
              - { name: deploy, executor: ansible, root: ../../modules/ansible/web, playbook: deploy.yml }
              - { name: dns, executor: ansible, root: ../../modules/ansible/web, playbook: deploy.yml, interface: dns }
            """);
        test.Write("interfaces/dns.schema.json", """
            { "type": "object", "properties": { "inputs": { "type": "object",
              "properties": { "primary": { "type": "object", "required": ["address"] } }, "required": ["primary"] } } }
            """);
        test.Write(Binding, binding);
        if (bound)
            test.Write(Installation, File.ReadAllText(Path.Combine(test.Root, Installation)) + "interfaces:\n  dns: house\n");
        return test;
    }

    private static string Errors(LabRepository repository) =>
        string.Join("\n", repository.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => $"{i.File}: {i.Message}"));

    [Fact]
    public void A_stage_that_declares_a_slot_gets_its_installations_binding_as_the_slots_var_and_its_secrets()
    {
        using var test = Repository();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Equal("", Errors(repository));
        Assert.True(repository.IsValid(Address));
        var service = repository.Service(Address)!;
        Assert.Equal(new[] { "deploy", "dns" }, service.Stages.Select(s => s.Name));
        var dns = service.Stages[1];
        Assert.Equal("10.1.0.53", dns.Inputs["dns"]!["primary"]!["address"]!.GetValue<string>());
        Assert.Equal("web.example.com", dns.Inputs["records"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("dns/house/token", dns.Secrets["dns_token"].Key);
        Assert.Equal("web/password", dns.Secrets["web_password"].Key);
        Assert.Equal("home/interfaces/dns/house", dns.Binding!.Address);
        // The other stage gets neither.
        Assert.Null(service.Stages[0].Inputs["dns"]);
        Assert.False(service.Stages[0].Secrets.ContainsKey("dns_token"));
        Assert.Null(service.Stages[0].Binding);
        Assert.Contains(repository.StoreUses, u => u.Key == "dns/house/token" && u.Consumer == "home/interfaces/dns/house");
    }

    [Fact]
    public void A_stage_whose_slot_its_installation_does_not_bind_is_not_run()
    {
        using var test = Repository(bound: false);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Equal("", Errors(repository));
        Assert.True(repository.IsValid(Address));
        Assert.Equal(new[] { "deploy" }, repository.Service(Address)!.Stages.Select(s => s.Name));
    }

    [Fact]
    public void An_installation_that_names_a_binding_that_does_not_exist_is_invalid()
    {
        using var test = Repository();
        test.Delete(Binding);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid("home/korriban"));
        Assert.Contains(repository.Issues, i => i.File == Installation && i.Message.Contains("labs/home/interfaces/dns/house.yaml does not exist"));
    }

    [Fact]
    public void A_binding_the_slots_schema_refuses_makes_the_services_that_use_it_invalid_and_points_at_the_binding()
    {
        using var test = Repository(binding: "schemaVersion: 1\ninputs:\n  primary: { name: dns-01 }\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid(Address));
        Assert.True(repository.IsValid(ServiceDefinitionTests.Guest));
        Assert.True(repository.IsValid("home/korriban"));
        var issue = Assert.Single(repository.IssuesFor(Address), i => i.Severity == IssueSeverity.Error);
        Assert.Equal(Binding, issue.File);
        Assert.Equal(3, issue.Line);
        Assert.Contains("interface dns of web-dns@1.0", issue.Message);
    }

    [Fact]
    public void A_binding_outside_its_own_schema_makes_the_services_that_use_it_invalid()
    {
        using var test = Repository(binding: "schemaVersion: 1\nprimary: 10.1.0.53\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid(Address));
        Assert.Contains(repository.Issues, i => i.File == Binding);
        Assert.Contains(repository.IssuesFor(Address), i => i.Message.Contains("which has errors"));
    }

    [Theory]
    [InlineData("schemaVersion: 1\nworkflow: web-dns@1\ninputs: { port: 1, dns: x }\nsecrets: { web_password: { store: web/password } }\n", "inputs.dns is the extra var")]
    [InlineData("schemaVersion: 1\nworkflow: web-dns@1\ninputs: { port: 1 }\nsecrets: { web_password: { store: web/password }, dns_token: { store: x/y } }\n", "dns_token, a secret of")]
    public void A_service_that_sets_what_its_binding_sets_is_invalid(string service, string expected)
    {
        using var test = Repository(service);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid(Address));
        Assert.Contains(expected, Errors(repository));
    }

    [Fact]
    public void A_binding_cannot_hand_a_stage_a_proxmox_token()
    {
        using var test = Repository(binding: "schemaVersion: 1\ninputs: { primary: { address: 10.1.0.53 } }\nsecrets:\n  dns_token: { store: proxmox/korriban/privileged-token }\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid(Address));
        Assert.Contains(repository.Issues, i => i.File == Binding && i.Message.Contains("cannot use a Proxmox API token"));
    }

    [Fact]
    public void A_slot_needs_its_schema_beside_the_workflows()
    {
        using var test = Repository();
        test.Delete("interfaces/dns.schema.json");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid(Address));
        Assert.Contains("interfaces/dns.schema.json, which says what its bindings hold, does not exist", Errors(repository));
    }

    [Fact]
    public void Only_a_services_stage_fills_a_slot()
    {
        using var test = Repository();
        test.Write("workflows/app/workflow.yaml", File.ReadAllText(Path.Combine(test.Root, "workflows/app/workflow.yaml"))
            .Replace("    prepare: prepare.yml", "    prepare: prepare.yml\n    interface: dns"));
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains("only a stage of a workflow with manages: service fills one", Errors(repository));
    }

    [Fact]
    public void An_opentofu_stage_fills_no_slot()
    {
        using var test = Repository();
        test.Write("workflows/web-dns/workflow.yaml", """
            schemaVersion: 1
            version: "1.0"
            manages: service
            stages:
              - { name: dns, executor: opentofu, root: ../../modules/opentofu/web-rules, interface: dns }
            """);
        Assert.False(LabRepositoryLoader.Load(test.Root).IsValid(Address));
    }

    [Fact]
    public void A_binding_changes_the_hash_of_the_services_it_fills_and_of_no_other()
    {
        using var unbound = Repository(bound: false);
        using var bound = Repository();
        var before = LabRepositoryLoader.Load(unbound.Root);
        var after = LabRepositoryLoader.Load(bound.Root);
        Assert.NotEqual(PipelineInputs.Build(before, before.Service(Address)!).Hash, PipelineInputs.Build(after, after.Service(Address)!).Hash);

        var first = PipelineInputs.Build(after, after.Service(Address)!).Hash;
        bound.Write(Binding, BindingYaml.Replace("10.1.0.53", "10.1.0.54"));
        var moved = LabRepositoryLoader.Load(bound.Root);
        Assert.NotEqual(first, PipelineInputs.Build(moved, moved.Service(Address)!).Hash);
        // Its description runs nothing.
        bound.Write(Binding, BindingYaml.Replace("The house's DNS servers", "Elsewhere"));
        var described = LabRepositoryLoader.Load(bound.Root);
        Assert.Equal(first, PipelineInputs.Build(described, described.Service(Address)!).Hash);

        // A service without the slot keeps its hash whether or not its installation binds one.
        using var plainUnbound = ServiceDefinitionTests.Repository();
        using var plainBound = Repository(ServiceDefinitionTests.Service);
        var a = LabRepositoryLoader.Load(plainUnbound.Root);
        var b = LabRepositoryLoader.Load(plainBound.Root);
        Assert.Equal(PipelineInputs.Build(a, a.Service(Address)!).Hash, PipelineInputs.Build(b, b.Service(Address)!).Hash);
    }

    [Theory]
    [InlineData("labs/home/interfaces/Dns/house.yaml")]
    [InlineData("labs/home/interfaces/dns/House.yaml")]
    public void A_binding_with_an_invalid_name_is_an_error(string file)
    {
        using var test = Repository(bound: false);
        test.Write(file, BindingYaml);
        Assert.Contains(LabRepositoryLoader.Load(test.Root).Issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("not a valid"));
    }
}
