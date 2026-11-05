using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

public class GuestConfigurationTests
{
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string WorkflowFile = "workflows/app/workflow.yaml";
    private const string Address = "home/korriban/krb-app-01";

    /// <summary>A guest whose workflow sets it up with Ansible: the fixture for configure runs, too.</summary>
    public static TestRepository Repository(string policy = "", string extraWorkflow = "", string destructive = "false")
    {
        var test = TestRepository.Minimal();
        Edit(test, GuestFile, "workflow: test-vm@1", "workflow: app@1");
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile)) + """
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
        test.Write(WorkflowFile, $"""
            schemaVersion: 1
            version: "1.0"
            manages: vm
            inputs: inputs.schema.json
            secrets: [tailscale_key]
            stages:
              - name: machine
                executor: opentofu
                root: ../../modules/opentofu/test-vm
              - name: setup
                executor: ansible
                root: ../../modules/ansible/app
                playbook: site.yml
                prepare: prepare.yml
                destructive: {destructive}
            actions:
              update-system:
                stage: setup
                playbook: site.yml
                snapshot: applied
                destructive: false
            {extraWorkflow}
            """);
        test.Write("workflows/app/inputs.schema.json", """
            { "type": "object", "properties": { "inputs": { "type": "object", "properties": { "greeting": { "type": "string" } }, "required": ["greeting"] } } }
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
    public void A_guest_whose_workflow_sets_it_up_is_valid_and_how_it_is_reached_does_not_plan_it()
    {
        using var test = Repository();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        Assert.True(repository.IsValid(Address));
        var guest = repository.Guest(Address)!;
        Assert.Equal("app@1.0", guest.Workflow!.Label);
        var inputs = ConfigureInputs.Build(repository, guest);
        Assert.Equal("10.1.0.31", inputs.Address);
        Assert.Equal(22, inputs.Port);
        Assert.Equal("access/app/ssh-key", inputs.SshKey!.Key);
        Assert.Equal("access/app/host-key", inputs.HostKey!.Key);
        Assert.Equal(WorkflowDefinition.SetupAction, inputs.Configure.OnChange!.Id);
        Assert.Equal("site.yml", inputs.Configure.OnChange.Playbook);
        Assert.Equal("modules/ansible/app", inputs.Stage.Root);
        Assert.Equal("hello", inputs.Vars["greeting"]!.GetValue<string>());
        Assert.True(inputs.Configure.Actions["update-system"].UsesAppliedSnapshot);

        var json = guest.Json.DeepClone().AsObject();
        json["access"]!["address"] = "10.1.0.32";
        json["access"]!["port"] = 2222;
        json["secrets"]!["tailscale_key"] = new System.Text.Json.Nodes.JsonObject { ["store"] = "app/other" };
        // Telling Octopushy how to reach a guest, or what its playbook is given, must not plan it: adding this to a
        // running guest with automatic approval would otherwise start a provision run.
        Assert.Equal(
            ProvisionInputs.Build(repository, guest, "c").Hash,
            ProvisionInputs.Build(repository, guest with { Json = json }, "c").Hash);
    }

    [Theory]
    [InlineData(GuestFile, "playbook changes", false)]
    [InlineData("modules/ansible/app/site.yml", "- hosts: all\n# changed\n", true)]
    [InlineData("workflows/app/inputs.schema.json", """{ "type": "object" }""", true)]
    [InlineData("workflows/app/files/motd", "hello\n", true)]
    [InlineData("labs/home/proxmox/korriban/guests/krb-app-01/files/motd", "hello\n", true)]
    [InlineData("modules/opentofu/test-vm/main.tf", "# another machine\n", false)]
    [InlineData("modules/ansible/other/site.yml", "- hosts: all\n", false)]
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
    [InlineData(WorkflowFile, "prepare: prepare.yml\n    destructive", "prepare: prepare.yml\n    snapshot: applied\n    destructive")]
    [InlineData(WorkflowFile, "executor: ansible", "executor: opentofu")]
    [InlineData(WorkflowFile, "playbook: site.yml\n    prepare", "playbook: ../../../etc/passwd\n    prepare")]
    [InlineData(WorkflowFile, "playbook: site.yml\n    prepare", "playbook: missing.yml\n    prepare")]
    [InlineData(WorkflowFile, "playbook: site.yml\n    snapshot", "playbook: missing.yml\n    snapshot")]
    [InlineData(WorkflowFile, "stage: setup", "stage: machine")]
    [InlineData(WorkflowFile, "update-system:", "setup:")]
    [InlineData(WorkflowFile, "secrets: [tailscale_key]", "secrets: [missing_key]")]
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
    [InlineData(GuestFile, "workflow: app@1", "workflow: app")]
    [InlineData(GuestFile, "workflow: app@1", "workflow: app@2")]
    [InlineData(GuestFile, "workflow: app@1", "workflow: nothing@1")]
    public void Invalid_definitions_are_refused(string file, string from, string to)
    {
        using var test = Repository();
        Edit(test, file, from, to);
        Assert.NotEmpty(Errors(test));
        Assert.False(LabRepositoryLoader.Load(test.Root).IsValid(Address));
    }

    [Fact]
    public void The_playbook_of_the_setup_stage_cannot_need_a_snapshot()
    {
        using var test = Repository();
        Edit(test, WorkflowFile, "prepare: prepare.yml\n    destructive", "snapshot: applied\n    destructive");
        Assert.Contains("snapshot", Errors(test));
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
    public void Secrets_without_a_setup_stage_are_refused_and_inputs_are_not()
    {
        using var test = Repository();
        Edit(test, GuestFile, "workflow: app@1", "workflow: test-vm@1");
        var errors = Errors(test);
        Assert.Contains("secrets are for a workflow's Ansible stage, and test-vm@1.0 has none", errors);
        Assert.DoesNotContain("inputs", errors);
    }

    [Fact]
    public void A_machine_stage_keeps_its_rules()
    {
        using var test = Repository();
        Edit(test, "workflows/test-vm/workflow.yaml", "executor: opentofu", "executor: ansible");
        Assert.NotEmpty(Errors(test));
    }

    [Fact]
    public void Store_references_of_a_guest_are_indexed()
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
            .Replace("access:\n  user: ops", $"schedules:\n{schedules}access:\n  user: ops"));
        return test;
    }

    private const string DeclaredKey = "  sshKey: { store: access/app/ssh-key }\n";

    private static TestRepository WithManagedKey()
    {
        var test = Repository();
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile)).Replace(DeclaredKey, ""));
        return test;
    }

    [Fact]
    public void A_schedule_names_an_action_of_the_workflow_and_a_time()
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
    [InlineData("    - { action: setup, cron: \"not a cron\" }\n", "is not a cron expression")]
    [InlineData("    - { action: setup, cron: \"0 6 * * * *\" }\n", "is not a cron expression")]
    [InlineData("    - { action: setup, cron: \"0 6 31 2 *\" }\n", "never falls due")]
    [InlineData("    - { action: setup, cron: \"0 6 * * *\" }\n    - { action: setup, cron: \"0 6 * * *\" }\n", "listed twice")]
    public void A_schedule_that_cannot_run_is_refused(string schedules, string message)
    {
        using var test = WithSchedules(schedules);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(repository.IssuesFor(Address), i => i.Message.Contains(message));
        Assert.False(repository.IsValid(Address));
    }

    [Theory]
    [InlineData("    - { action: setup }\n")]
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
    public void A_schedule_needs_a_setup_stage()
    {
        using var test = TestRepository.Minimal();
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile)) + "schedules:\n  - { action: setup, cron: \"0 6 * * *\" }\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(repository.IssuesFor(Address), i => i.Message.Contains("schedules are for a workflow's Ansible stage, and test-vm@1.0 has none"));
    }

    [Fact]
    public void A_schedule_needs_a_real_time_zone()
    {
        using var test = WithSchedules("    - { action: setup, cron: \"0 6 * * *\" }\n");
        test.Write("labs/home/lab.yaml", File.ReadAllText(Path.Combine(test.Root, "labs/home/lab.yaml")) + "timezone: Mars/Olympus\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(repository.IssuesFor(Address), i => i.Message.Contains("Mars/Olympus"));
    }

    [Fact]
    public void Changing_a_schedule_configures_nothing()
    {
        using var without = Repository();
        using var with = WithSchedules("    - { action: setup, cron: \"0 6 * * *\" }\n");
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

    [Fact]
    public void Times_come_back_in_utc_whatever_the_labs_zone_because_only_utc_can_be_stored()
    {
        var madrid = GuestSchedules.ZoneOf("Europe/Madrid");
        var from = DateTimeOffset.Parse("2026-06-01T00:00:00Z");

        Assert.Equal(TimeSpan.Zero, GuestSchedules.Next("0 6 * * *", madrid, from)!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, GuestSchedules.Latest("0 6 * * *", madrid, from, from.AddDays(1))!.Value.Offset);
    }

    [Fact]
    public void A_time_in_the_hour_the_clocks_skip_falls_once_as_they_jump()
    {
        var madrid = GuestSchedules.ZoneOf("Europe/Madrid");
        // 02:00 becomes 03:00, so 02:30 never happens on this morning.
        var jump = DateTimeOffset.Parse("2026-03-29T01:00:00Z");

        Assert.Equal(jump, GuestSchedules.Next("30 2 * * *", madrid, jump.AddHours(-3)));
        Assert.Equal(jump, GuestSchedules.Latest("30 2 * * *", madrid, jump.AddHours(-1), jump.AddHours(1)));
        Assert.Equal(DateTimeOffset.Parse("2026-03-30T00:30:00Z"), GuestSchedules.Next("30 2 * * *", madrid, jump));
    }

    [Fact]
    public void A_time_in_the_hour_the_clocks_repeat_falls_once_the_first_time_round()
    {
        var madrid = GuestSchedules.ZoneOf("Europe/Madrid");
        // 03:00 goes back to 02:00, so 02:30 happens at 00:30 and again at 01:30 UTC.
        var first = DateTimeOffset.Parse("2026-10-25T00:30:00Z");

        Assert.Equal(first, GuestSchedules.Next("30 2 * * *", madrid, first.AddHours(-3)));
        Assert.Null(GuestSchedules.Latest("30 2 * * *", madrid, first, first.AddHours(2)));
        Assert.Equal(DateTimeOffset.Parse("2026-10-26T01:30:00Z"), GuestSchedules.Next("30 2 * * *", madrid, first));
    }

    [Fact]
    public void A_schedule_that_runs_every_hour_keeps_going_through_the_repeated_hour()
    {
        var madrid = GuestSchedules.ZoneOf("Europe/Madrid");
        var first = DateTimeOffset.Parse("2026-10-25T00:30:00Z");

        Assert.Equal(first.AddHours(1), GuestSchedules.Next("30 * * * *", madrid, first));
    }

    [Fact]
    public void A_guest_set_up_by_its_workflow_needs_no_key_of_its_own()
    {
        using var test = WithManagedKey();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        var guest = repository.Guest(Address)!;
        Assert.True(ManagedAccess.Wanted(repository, guest, false));
        Assert.Null(ConfigureInputs.Build(repository, guest).SshKey);
    }

    [Fact]
    public void Only_a_guest_octopushy_configures_with_its_own_key_changes_its_provisioning_hash()
    {
        using var declared = Repository();
        using var renamed = Repository();
        using var managed = WithManagedKey();
        Edit(renamed, GuestFile, "access/app/ssh-key", "access/app/other-key");
        string Hash(TestRepository test)
        {
            var repository = LabRepositoryLoader.Load(test.Root);
            return ProvisionInputs.Build(repository, repository.Guest(Address)!, "c").Hash;
        }

        // Which key the guest names is how a playbook logs in, and nothing about the machine.
        Assert.Equal(Hash(declared), Hash(renamed));
        Assert.NotEqual(Hash(declared), Hash(managed));
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
