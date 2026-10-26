using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

public class LabRepositoryLoaderTests
{
    [Fact]
    public void The_example_lab_repository_is_valid()
    {
        var repository = LabRepositoryLoader.Load(TestRepository.ExampleLab);
        Assert.DoesNotContain(repository.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.True(repository.RepositoryValid);
        Assert.Equal(2, repository.Guests.Count);
        Assert.All(repository.Resources, r => Assert.True(r.Valid, r.Address));
        var guest = repository.Guest("home/korriban/krb-app-01")!;
        Assert.Equal(310, guest.Vmid);
        Assert.Equal("home.korriban.krb-app-01", guest.Stack);
        Assert.Contains(repository.StoreUses, u => u.Key == "proxmox/korriban/api-token" && u.Consumer == "home/korriban");
    }

    [Fact]
    public void The_end_to_end_lab_repository_is_valid()
    {
        var repository = LabRepositoryLoader.Load(Path.Combine(TestRepository.SourceRoot, "tests", "e2e", "lab"));
        Assert.Empty(repository.Issues);
        Assert.Equal("e2e/korriban/e2e-vm-01", Assert.Single(repository.Guests).Address);
    }

    [Fact]
    public void A_minimal_repository_is_valid()
    {
        using var test = TestRepository.Minimal();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        Assert.Equal(["home", "home/korriban", "home/korriban/krb-app-01"], repository.Resources.Select(r => r.Address));
    }

    [Theory]
    [InlineData("intervalSeconds: 59")]
    [InlineData("intervalSeconds: 604801")]
    [InlineData("enabled: 'yes'")]
    [InlineData("binaryUrl: https://untrusted.example/binary")]
    public void Invalid_reporter_overrides_are_rejected(string settings)
    {
        using var test = TestRepository.Minimal();
        const string path = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        test.Write(path, File.ReadAllText(Path.Combine(test.Root, path)) + $"\nsetup: {{ diskReporter: {{ {settings} }} }}\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid("home/korriban/krb-app-01"));
        Assert.Contains(repository.Issues, i => i.Message.Contains("diskReporter"));
    }

    [Fact]
    public void Without_octopushy_yaml_nothing_is_valid()
    {
        using var test = TestRepository.Minimal();
        test.Delete("octopushy.yaml");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.RepositoryValid);
        Assert.All(repository.Resources, r => Assert.False(r.Valid));
    }

    [Fact]
    public void Schema_errors_point_at_the_line()
    {
        using var test = TestRepository.Minimal();
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml", """
            schemaVersion: 1
            type: vm
            host: korriban
            vmid: 12
            provision:
              recipe: test-vm
              state: { backend: postgres }
            """);
        var repository = LabRepositoryLoader.Load(test.Root);
        var issue = Assert.Single(repository.Issues);
        Assert.Equal(4, issue.Line);
        Assert.Contains("vmid", issue.Message);
        Assert.False(repository.IsValid("home/korriban/krb-app-01"));
        Assert.True(repository.IsValid("home/korriban"));
    }

    [Fact]
    public void A_broken_guest_does_not_affect_its_siblings()
    {
        using var test = TestRepository.Minimal();
        test.Write("labs/home/proxmox/korriban/guests/krb-app-02/guest.yaml", "schemaVersion: 1\ntype: vm\nhost: elsewhere\nvmid: 311\nprovision: { recipe: test-vm, state: { backend: postgres } }\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid("home/korriban/krb-app-02"));
        Assert.True(repository.IsValid("home/korriban/krb-app-01"));
        Assert.Contains(repository.Issues, i => i.Message.Contains("'elsewhere' is not listed"));
    }

    [Fact]
    public void Duplicate_vmids_in_an_installation_are_errors_on_both_guests()
    {
        using var test = TestRepository.Minimal();
        test.Write("labs/home/proxmox/korriban/guests/krb-app-02/guest.yaml", "schemaVersion: 1\ntype: vm\nhost: korriban\nvmid: 310\nprovision: { recipe: test-vm, state: { backend: postgres } }\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid("home/korriban/krb-app-01"));
        Assert.False(repository.IsValid("home/korriban/krb-app-02"));
    }

    [Fact]
    public void References_to_missing_shared_definitions_are_errors()
    {
        using var test = TestRepository.Minimal();
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml", "schemaVersion: 1\ntype: vm\nhost: korriban\nvmid: 310\ntemplate: nope\nprovision: { recipe: nope, state: { backend: nope } }\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        var messages = repository.Issues.Select(i => i.Message).ToList();
        Assert.Contains(messages, m => m.Contains("template 'nope'"));
        Assert.Contains(messages, m => m.Contains("recipe 'nope'"));
        Assert.Contains(messages, m => m.Contains("state backend 'nope'"));
    }

    [Fact]
    public void System_store_keys_cannot_be_referenced()
    {
        using var test = TestRepository.Minimal();
        test.Write("labs/home/proxmox/korriban/installation.yaml", """
            schemaVersion: 1
            type: standalone
            connection:
              endpoint: https://10.1.0.10:8006/
              token: { store: system/agent-ca/key }
            hosts:
              korriban: {}
            """);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(repository.Issues, i => i.Message.Contains("system scope") && i.Line == 5);
        Assert.False(repository.IsValid("home/korriban"));
        Assert.False(repository.IsValid("home/korriban/krb-app-01"));
    }

    [Fact]
    public void A_root_that_declares_a_backend_is_refused()
    {
        using var test = TestRepository.Minimal();
        test.Write("modules/opentofu/test-vm/backend.tf", "terraform {\n  backend \"local\" {}\n}\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(repository.Issues, i => i.Message.Contains("must not declare a backend"));
        Assert.False(repository.IsValid("home/korriban/krb-app-01"));
    }

    [Fact]
    public void Recipe_inputs_schemas_validate_hardware_and_network()
    {
        using var test = TestRepository.Minimal();
        test.Write("recipes/test-vm/inputs.schema.json", """
            { "type": "object", "properties": { "hardware": { "type": "object", "required": ["disks"] } } }
            """);
        test.Write("recipes/test-vm/recipe.yaml", "schemaVersion: 1\noperation: provision\nexecutor: opentofu\nroot: ../../modules/opentofu/test-vm\ninputs: inputs.schema.json\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(repository.Issues, i => i.Message.Contains("disks") && i.Message.Contains("recipe test-vm"));
    }

    [Fact]
    public void A_standalone_installation_has_one_host()
    {
        using var test = TestRepository.Minimal();
        test.Write("labs/home/proxmox/korriban/installation.yaml", """
            schemaVersion: 1
            type: standalone
            connection:
              endpoint: https://10.1.0.10:8006/
              token: { store: proxmox/korriban/api-token }
            hosts:
              korriban: {}
              other: {}
            """);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(repository.Issues, i => i.Message.Contains("exactly one host"));
    }

    [Fact]
    public void Invalid_yaml_is_reported_and_the_resource_still_exists()
    {
        using var test = TestRepository.Minimal();
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml", "schemaVersion: 1\nvmid: [\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        var entry = repository.Resource("home/korriban/krb-app-01");
        Assert.NotNull(entry);
        Assert.False(entry.Valid);
        Assert.Contains(repository.Issues, i => i.Message.StartsWith("Invalid YAML"));
    }
}
