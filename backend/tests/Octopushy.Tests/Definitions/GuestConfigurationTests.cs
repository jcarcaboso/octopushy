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
        Assert.Equal("access/app/ssh-key", inputs.SshKey.Key);
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
    [InlineData(GuestFile, "  sshKey: { store: access/app/ssh-key }\n", "")]
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

    private static TestRepository WithSchedules(string schedules)
    {
        var test = Repository();
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile))
            .Replace("access:\n  user: ops", $"  schedules:\n{schedules}access:\n  user: ops"));
        return test;
    }

    [Fact]
    public void A_schedule_names_an_action_of_the_recipe_and_a_time()
    {
        using var test = WithSchedules("    - { action: update-system, cron: \"0 6 * * *\" }\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        Assert.Equal(
            new GuestScheduleDefinition("update-system", "0 6 * * *"),
            Assert.Single(GuestSchedules.Of(repository.Guest(Address)!)));
    }

    [Theory]
    [InlineData("    - { action: reinstall, cron: \"0 6 * * *\" }\n", "has no such action")]
    [InlineData("    - { action: configure, cron: \"not a cron\" }\n", "is not a cron expression")]
    [InlineData("    - { action: configure, cron: \"0 6 * * * *\" }\n", "is not a cron expression")]
    [InlineData("    - { action: configure, cron: \"0 6 31 2 *\" }\n", "never falls due")]
    [InlineData("    - { action: configure, cron: \"0 6 * * *\" }\n    - { action: configure, cron: \"0 6 * * *\" }\n", "listed twice")]
    public void A_schedule_that_cannot_run_is_refused(string schedules, string message)
    {
        using var test = WithSchedules(schedules);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(repository.IssuesFor(Address), i => i.Message.Contains(message));
        Assert.False(repository.IsValid(Address));
    }

    [Theory]
    [InlineData("    - { action: configure }\n")]
    [InlineData("    - { cron: \"0 6 * * *\" }\n")]
    [InlineData("    - { action: 5, cron: 6 }\n")]
    [InlineData("    - just text\n")]
    public void A_malformed_schedule_is_an_error_on_the_guest_and_not_a_failed_load(string schedules)
    {
        using var test = WithSchedules(schedules);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid(Address));
        Assert.True(repository.RepositoryValid);
    }

    [Fact]
    public void A_schedule_needs_a_recipe()
    {
        using var test = TestRepository.Minimal();
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile)) + "setup:\n  schedules:\n    - { action: configure, cron: \"0 6 * * *\" }\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(repository.IssuesFor(Address), i => i.Message.Contains("setup.schedules need a setup.recipe"));
    }

    [Fact]
    public void A_schedule_needs_a_real_time_zone()
    {
        using var test = WithSchedules("    - { action: configure, cron: \"0 6 * * *\" }\n");
        test.Write("labs/home/lab.yaml", File.ReadAllText(Path.Combine(test.Root, "labs/home/lab.yaml")) + "timezone: Mars/Olympus\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(repository.IssuesFor(Address), i => i.Message.Contains("Mars/Olympus"));
    }

    [Fact]
    public void Changing_a_schedule_configures_nothing()
    {
        using var without = Repository();
        using var with = WithSchedules("    - { action: configure, cron: \"0 6 * * *\" }\n");
        var before = LabRepositoryLoader.Load(without.Root);
        var after = LabRepositoryLoader.Load(with.Root);
        Assert.Equal(
            ConfigureInputs.Build(before, before.Guest(Address)!).Hash,
            ConfigureInputs.Build(after, after.Guest(Address)!).Hash);
    }

    [Theory]
    [InlineData("UTC", "2026-03-29T06:00:00+00:00")]
    // The clocks go forward that morning, and 06:00 is still 06:00 where the lab is.
    [InlineData("Europe/Madrid", "2026-03-29T04:00:00+00:00")]
    public void A_time_is_read_in_the_labs_zone(string zone, string expected)
    {
        var next = GuestSchedules.Next("0 6 * * *", GuestSchedules.ZoneOf(zone), DateTimeOffset.Parse("2026-03-28T23:00:00Z"));
        Assert.Equal(DateTimeOffset.Parse(expected), next);
    }
}
