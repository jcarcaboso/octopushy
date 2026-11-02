using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

public class ServiceDefinitionTests
{
    public const string Guest = "home/korriban/krb-svc-01";
    public const string GuestPath = "labs/home/proxmox/korriban/guests/krb-svc-01/guest.yaml";
    public const string AlphaPath = "labs/home/proxmox/korriban/guests/krb-svc-01/services/alpha/service.yaml";
    public const string BetaPath = "labs/home/proxmox/korriban/guests/krb-svc-01/services/beta/service.yaml";

    public const string GuestDefinition = """
        schemaVersion: 1
        type: container
        host: korriban
        vmid: 312
        hardware: { cores: 1, memory: 512 }
        network: { bridge: vmbr0, mode: static, address: 10.1.0.34/24, gateway: 10.1.0.1 }
        provision:
          recipe: test-ct
          state: { backend: postgres }
        access:
          user: root
          sshKey: { store: svc/ssh-key }
          hostKey: { store: svc/host-key }
        """;

    public static string Service(string extra = "", string recipe = "svc") => $$"""
        schemaVersion: 1
        deploy:
          recipe: {{recipe}}
          inputs: { greeting: hello }
          secrets:
            app_password: { store: svc/password }
        ports:
          - { port: 5432, protocol: tcp, sources: [10.15.40.10] }
        {{extra}}
        """;

    /// <summary>A guest with two services, on a shared configure recipe.</summary>
    public static TestRepository Repository(string guest = GuestDefinition, string actions = "")
    {
        var test = ContainerGuestTests.Repository();
        test.Delete("labs/home/proxmox/korriban/guests/krb-app-01");
        test.Delete("labs/home/proxmox/korriban/guests/krb-ct-01");
        test.Write(GuestPath, guest);
        test.Write(AlphaPath, Service());
        test.Write(BetaPath, Service().Replace("5432", "8428"));
        test.Write("modules/ansible/svc/deploy.yml", "- hosts: all\n");
        test.Write("modules/ansible/svc/remove.yml", "- hosts: all\n");
        test.Write("recipes/svc/inputs.schema.json", """{ "type": "object", "properties": { "greeting": { "type": "string" } }, "additionalProperties": false }""");
        test.Write("recipes/svc/recipe.yaml", $$"""
            schemaVersion: 1
            operation: configure
            executor: ansible
            root: ../../modules/ansible/svc
            inputs: inputs.schema.json
            secrets: [app_password]
            actions:
              deploy: { playbook: deploy.yml, destructive: false, onChange: true }
            {{actions}}
            """);
        return test;
    }

    private static List<string> Errors(TestRepository test) =>
        LabRepositoryLoader.Load(test.Root).Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message).ToList();

    [Fact]
    public void A_service_is_a_resource_under_its_guest()
    {
        using var test = Repository();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(Errors(test));
        var entry = repository.Resource($"{Guest}/alpha")!;
        Assert.Equal((ResourceKind.Service, Guest, true), (entry.Kind, entry.ParentAddress, entry.Valid));
        Assert.Equal(2, repository.ServicesOf(Guest).Count());
        Assert.Contains(repository.StoreUses, u => u.Key == "svc/password" && u.Consumer == $"{Guest}/alpha");
    }

    [Theory]
    [InlineData("dependsOn: []")]
    [InlineData("dns: []")]
    [InlineData("verify: { http: { url: https://example.com } }")]
    public void What_this_slice_does_not_do_is_refused_rather_than_ignored(string field)
    {
        using var test = Repository();
        test.Write(AlphaPath, Service(field));
        Assert.NotEmpty(Errors(test));
    }

    [Fact]
    public void A_port_with_interfaces_is_refused()
    {
        using var test = Repository();
        test.Write(AlphaPath, Service().Replace("sources: [10.15.40.10]", "interfaces: [eth0]"));
        Assert.NotEmpty(Errors(test));
    }

    [Fact]
    public void A_service_needs_a_configure_recipe_that_exists()
    {
        using var test = Repository();
        test.Write(AlphaPath, Service(recipe: "nothing"));
        Assert.Contains(Errors(test), e => e.Contains("'nothing' does not exist"));
        test.Write(AlphaPath, Service(recipe: "test-ct"));
        Assert.Contains(Errors(test), e => e.Contains("does not configure guests"));
    }

    [Fact]
    public void A_service_needs_its_secrets_and_its_guests_access()
    {
        using var test = Repository();
        test.Write(AlphaPath, "schemaVersion: 1\ndeploy: { recipe: svc }\n");
        Assert.Contains(Errors(test), e => e.Contains("needs deploy.secrets.app_password"));
        test.Write(GuestPath, GuestDefinition.Replace("  user: root\n", ""));
        Assert.Contains(Errors(test), e => e.Contains("A guest with services needs access.user"));
    }

    [Fact]
    public void A_service_cannot_use_a_proxmox_token_or_a_reserved_name()
    {
        using var test = Repository();
        test.Write(AlphaPath, Service().Replace("svc/password", "proxmox/korriban/read-only-token"));
        Assert.Contains(Errors(test), e => e.Contains("cannot use a Proxmox API token"));
        test.Write(AlphaPath, Service().Replace("inputs: { greeting: hello }", "inputs: { ansible_host: elsewhere }"));
        Assert.NotEmpty(Errors(test));
    }

    [Fact]
    public void Inputs_follow_the_recipes_schema()
    {
        using var test = Repository();
        test.Write(AlphaPath, Service().Replace("greeting: hello", "greeting: 3"));
        Assert.Contains(Errors(test), e => e.Contains("recipe svc"));
    }

    [Fact]
    public void Two_services_on_a_guest_cannot_declare_one_port()
    {
        using var test = Repository();
        test.Write(BetaPath, Service());
        Assert.Contains(Errors(test), e => e.Contains("5432/tcp is also declared"));
    }

    [Theory]
    [InlineData("remove: { playbook: remove.yml, destructive: false, removal: true }", "has to be destructive")]
    [InlineData("deploy2: { playbook: deploy.yml, destructive: true, removal: true, onChange: true }", "")]
    public void A_removal_action_has_to_say_it_is_destructive(string action, string message)
    {
        using var test = Repository(actions: "  " + action);
        Assert.Contains(Errors(test), e => e.Contains(message));
    }

    [Fact]
    public void The_policy_of_a_guest_reaches_its_services_and_the_nearest_wins()
    {
        using var test = Repository(GuestDefinition + "\npolicy: { approval: manual }");
        test.Write(AlphaPath, Service("policy: { approval: automatic, destructive: manual }"));
        var repository = LabRepositoryLoader.Load(test.Root);
        var alpha = PolicyResolver.For(repository, repository.Service($"{Guest}/alpha")!);
        Assert.Equal(("automatic", "manual", true), (alpha.Approval.Value, alpha.Destructive.Value, alpha.DestructiveExplicit));
        Assert.Equal("manual", PolicyResolver.For(repository, repository.Service($"{Guest}/beta")!).Approval.Value);
    }

    [Fact]
    public void Changing_a_service_does_not_change_its_guests_setup_or_its_sibling()
    {
        using var test = Repository(GuestDefinition + "\nsetup: { recipe: bare }");
        test.Write("recipes/bare/recipe.yaml", """
            schemaVersion: 1
            operation: configure
            executor: ansible
            root: ../../modules/ansible/svc
            actions:
              configure: { playbook: deploy.yml, destructive: false, onChange: true }
            """);
        var before = LabRepositoryLoader.Load(test.Root);
        test.Write(AlphaPath, Service().Replace("greeting: hello", "greeting: bye"));
        test.Write("labs/home/proxmox/korriban/guests/krb-svc-01/services/alpha/notes.txt", "mine");
        var after = LabRepositoryLoader.Load(test.Root);

        Assert.Equal(ConfigureInputs.Build(before, before.Guest(Guest)!).Hash, ConfigureInputs.Build(after, after.Guest(Guest)!).Hash);
        Assert.Equal(ConfigureInputs.Build(before, before.Service($"{Guest}/beta")!).Hash, ConfigureInputs.Build(after, after.Service($"{Guest}/beta")!).Hash);
        Assert.NotEqual(ConfigureInputs.Build(before, before.Service($"{Guest}/alpha")!).Hash, ConfigureInputs.Build(after, after.Service($"{Guest}/alpha")!).Hash);
    }

    [Fact]
    public void A_policy_change_starts_no_run_but_a_port_does()
    {
        using var test = Repository();
        var before = LabRepositoryLoader.Load(test.Root);
        test.Write(AlphaPath, Service("policy: { approval: manual }"));
        var policy = LabRepositoryLoader.Load(test.Root);
        Assert.Equal(ConfigureInputs.Build(before, before.Service($"{Guest}/alpha")!).Hash, ConfigureInputs.Build(policy, policy.Service($"{Guest}/alpha")!).Hash);
        test.Write(AlphaPath, Service().Replace("10.15.40.10", "10.15.40.11"));
        var port = LabRepositoryLoader.Load(test.Root);
        Assert.NotEqual(ConfigureInputs.Build(before, before.Service($"{Guest}/alpha")!).Hash, ConfigureInputs.Build(port, port.Service($"{Guest}/alpha")!).Hash);
    }

    [Fact]
    public void The_digest_covers_the_code_and_what_each_secret_becomes()
    {
        using var test = Repository();
        var digest = () =>
        {
            var repository = LabRepositoryLoader.Load(test.Root);
            return ConfigureInputs.Build(repository, repository.Service($"{Guest}/alpha")!).CodeDigest;
        };
        var before = digest();
        File.AppendAllText(Path.Combine(test.Root, "modules/ansible/svc/deploy.yml"), "# changed\n");
        var code = digest();
        Assert.NotEqual(before, code);
        test.Write(AlphaPath, Service().Replace("app_password:", "other_name:").Replace("deploy:\n", "deploy:\n"));
        Assert.NotEqual(code, digest());
    }

    [Fact]
    public void The_example_lab_has_ha_db_as_a_container_with_two_services()
    {
        var repository = LabRepositoryLoader.Load(TestRepository.ExampleLab);
        Assert.DoesNotContain(repository.Issues, i => i.Severity == IssueSeverity.Error);
        var guest = repository.Guest("home/ziost/ha-db")!;
        Assert.Equal(("proxmox-lxc", "debian-base", true), (guest.Recipe, guest.SetupRecipe, guest.IsContainer));
        Assert.Equal(["home/ziost/ha-db/postgresql", "home/ziost/ha-db/victoriametrics"], repository.ServicesOf(guest.Address).Select(s => s.Address).Order());
        var victoria = repository.Recipes["victoriametrics"].Configure!;
        Assert.True(victoria.Actions["shrink-retention"].Destructive);
        Assert.False(victoria.Actions["deploy"].Destructive);
        Assert.False(victoria.Actions["shrink-retention"].OnChange);
    }
}
