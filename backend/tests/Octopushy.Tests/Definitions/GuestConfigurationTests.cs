using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

public class GuestConfigurationTests
{
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string RecipeFile = "recipes/app/recipe.yaml";
    private const string Address = "home/korriban/krb-app-01";

    /// <summary>A guest with a setup recipe: the fixture for configure runs, too.</summary>
    public static TestRepository Repository(string policy = "", string extraRecipe = "", string destructive = "false")
    {
        var test = TestRepository.Minimal();
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile)) + """
            setup:
              recipe: app
              inputs: { greeting: hello }
              secrets:
                tailscale_key: { store: app/tailscale }
              secretFiles:
                git_key_src: { store: app/git-key }
            access:
              user: ops
              sshKey: { store: access/app/ssh-key }
              hostKey: { store: access/app/host-key }

            """ + policy + "\n");
        test.Write(RecipeFile, $"""
            schemaVersion: 1
            operation: configure
            executor: ansible
            root: ../../modules/ansible/app
            inputs: inputs.schema.json
            secrets: [tailscale_key]
            actions:
              configure:
                playbook: site.yml
                prepare: prepare.yml
                destructive: {destructive}
                onChange: true
              update-system:
                playbook: site.yml
                snapshot: applied
                destructive: false
            {extraRecipe}
            """);
        test.Write("recipes/app/inputs.schema.json", """
            { "type": "object", "properties": { "greeting": { "type": "string" } }, "required": ["greeting"] }
            """);
        test.Write("modules/ansible/app/site.yml", "- hosts: all\n");
        test.Write("modules/ansible/app/prepare.yml", "- hosts: localhost\n");
        return test;
    }

    private static void Edit(TestRepository test, string file, string from, string to)
    {
        var text = File.ReadAllText(Path.Combine(test.Root, file));
        Assert.Contains(from, text);
        test.Write(file, text.Replace(from, to));
    }

    private static string Errors(TestRepository test) =>
        string.Join("\n", LabRepositoryLoader.Load(test.Root).Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message));

    [Fact]
    public void A_guest_with_a_configure_recipe_is_valid_and_keeps_its_provisioning_hash()
    {
        using var test = Repository();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        Assert.True(repository.IsValid(Address));
        var guest = repository.Guest(Address)!;
        var inputs = ConfigureInputs.Build(repository, guest);
        Assert.Equal("10.1.0.31", inputs.Address);
        Assert.Equal(22, inputs.Port);
        Assert.Equal("access/app/ssh-key", inputs.SshKey!.Key);
        Assert.Equal("access/app/host-key", inputs.HostKey.Key);
        Assert.Equal("configure", inputs.Configure.OnChange!.Id);
        Assert.True(inputs.Configure.Actions["update-system"].UsesAppliedSnapshot);

        using var plain = TestRepository.Minimal();
        var unconfigured = LabRepositoryLoader.Load(plain.Root);
        var json = guest.Json.DeepClone().AsObject();
        json["access"]!["address"] = "10.1.0.32";
        json["access"]!["port"] = 2222;
        // Telling Octopushy how to reach a guest must not plan it: adding this to a running guest
        // with automatic approval would otherwise start a provision run.
        Assert.Equal(
            ProvisionInputs.Build(unconfigured, unconfigured.Guest(Address)!, "c").Hash,
            ProvisionInputs.Build(repository, guest with { Json = json }, "c").Hash);
    }

    [Theory]
    [InlineData(GuestFile, "playbook changes", false)]
    [InlineData("modules/ansible/app/site.yml", "- hosts: all\n# changed\n", true)]
    [InlineData("recipes/app/inputs.schema.json", """{ "type": "object" }""", true)]
    [InlineData("labs/home/proxmox/korriban/guests/krb-app-01/files/motd", "hello\n", true)]
    public void The_hash_follows_what_a_run_is_built_from(string file, string content, bool changes)
    {
        using var test = Repository();
        var hash = () =>
        {
            var repository = LabRepositoryLoader.Load(test.Root);
            return ConfigureInputs.Build(repository, repository.Guest(Address)!).Hash;
        };
        var before = hash();
        if (file == GuestFile)
            Edit(test, GuestFile, "memory: 2048", "memory: 4096");
        else
            test.Write(file, content);
        Assert.Equal(changes, before != hash());
    }

    [Theory]
    [InlineData(RecipeFile, "destructive: false\n    onChange", "onChange")]
    [InlineData(RecipeFile, "prepare: prepare.yml", "prepare: prepare.yml\n    snapshot: applied")]
    [InlineData(RecipeFile, "executor: ansible", "executor: opentofu")]
    [InlineData(RecipeFile, "playbook: site.yml\n    prepare", "playbook: ../../../etc/passwd\n    prepare")]
    [InlineData(RecipeFile, "playbook: site.yml\n    prepare", "playbook: missing.yml\n    prepare")]
    [InlineData(RecipeFile, "snapshot: applied", "snapshot: applied\n    onChange: true")]
    [InlineData(RecipeFile, "secrets: [tailscale_key]", "secrets: [missing_key]")]
    [InlineData(GuestFile, "greeting: hello", "greeting: 3")]
    [InlineData(GuestFile, "greeting: hello", "ansible_ssh_common_args: x, greeting: hello")]
    [InlineData(GuestFile, "tailscale_key:", "octopushy_key:")]
    [InlineData(GuestFile, "git_key_src:", "tailscale_key:")]
    [InlineData(GuestFile, "app/tailscale", "proxmox/korriban/privileged-token")]
    [InlineData(GuestFile, "app/git-key", "proxmox/korriban/read-only-token")]
    [InlineData(GuestFile, "access/app/ssh-key", "proxmox/korriban/privileged-token")]
    [InlineData(GuestFile, "app/tailscale", "system/agents/key")]
    [InlineData(GuestFile, "  user: ops\n", "")]
    [InlineData(GuestFile, "  hostKey: { store: access/app/host-key }\n", "")]
    [InlineData(GuestFile, "recipe: app", "recipe: test-vm")]
    [InlineData(GuestFile, "recipe: app", "recipe: nothing")]
    public void Invalid_definitions_are_refused(string file, string from, string to)
    {
        using var test = Repository();
        Edit(test, file, from, to);
        Assert.NotEmpty(Errors(test));
        Assert.False(LabRepositoryLoader.Load(test.Root).IsValid(Address));
    }

    [Fact]
    public void An_action_that_follows_changes_cannot_need_a_snapshot()
    {
        using var test = Repository();
        Edit(test, RecipeFile, "    onChange: true\n", "");
        Edit(test, RecipeFile, "snapshot: applied", "snapshot: applied\n    onChange: true");
        Assert.Contains("a new machine has none", Errors(test));
    }

    [Fact]
    public void A_guest_needs_an_address_to_be_configured()
    {
        using var test = Repository();
        Edit(test, GuestFile, "mode: static, address: 10.1.0.31/24, gateway: 10.1.0.1", "mode: dhcp");
        Assert.Contains("access.address", Errors(test));
        Edit(test, GuestFile, "  user: ops\n", "  user: ops\n  address: guest.home.arpa\n");
        Assert.Empty(Errors(test));
    }

    [Fact]
    public void Setup_values_without_a_recipe_are_refused()
    {
        using var test = Repository();
        Edit(test, GuestFile, "  recipe: app\n", "");
        Assert.Contains("setup.recipe", Errors(test));
    }

    [Fact]
    public void A_provisioning_recipe_keeps_its_rules()
    {
        using var test = Repository();
        Edit(test, "recipes/test-vm/recipe.yaml", "executor: opentofu", "executor: ansible");
        Assert.NotEmpty(Errors(test));
    }

    [Fact]
    public void Store_references_in_setup_are_indexed()
    {
        using var test = Repository();
        var uses = LabRepositoryLoader.Load(test.Root).StoreUses.Select(u => u.Key).ToList();
        Assert.Contains("app/tailscale", uses);
        Assert.Contains("app/git-key", uses);
        Assert.Contains("access/app/ssh-key", uses);
        Assert.Contains("access/app/host-key", uses);
    }

    private const string DeclaredKey = "  sshKey: { store: access/app/ssh-key }\n";

    private static TestRepository WithManagedKey()
    {
        var test = Repository();
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile)).Replace(DeclaredKey, ""));
        return test;
    }

    [Fact]
    public void A_guest_with_a_recipe_needs_no_key_of_its_own()
    {
        using var test = WithManagedKey();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        var guest = repository.Guest(Address)!;
        Assert.True(ManagedAccess.Wanted(guest));
        Assert.Null(ConfigureInputs.Build(repository, guest).SshKey);
    }

    [Fact]
    public void Only_a_guest_octopushy_configures_with_its_own_key_changes_its_provisioning_hash()
    {
        using var plain = TestRepository.Minimal();
        using var declared = Repository();
        using var managed = WithManagedKey();
        string Hash(TestRepository test)
        {
            var repository = LabRepositoryLoader.Load(test.Root);
            return ProvisionInputs.Build(repository, repository.Guest(Address)!, "c").Hash;
        }

        Assert.Equal(Hash(plain), Hash(declared));
        Assert.NotEqual(Hash(plain), Hash(managed));
    }

    [Fact]
    public void Octopushys_key_is_not_in_the_definitions_variables()
    {
        using var test = WithManagedKey();
        var repository = LabRepositoryLoader.Load(test.Root);
        var variables = ProvisionInputs.Build(repository, repository.Guest(Address)!, "c").Variables;
        Assert.DoesNotContain(variables["guest"]!["sshAuthorizedKeys"]!.AsArray(), k => k!.GetValue<string>().Contains("octopushy"));
    }
}
