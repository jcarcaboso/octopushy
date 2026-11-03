using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

public class ServiceDefinitionTests
{
    internal const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    internal const string Guest = "home/korriban/krb-app-01";
    internal const string Address = Guest + "/web";
    internal const string ServiceFile = "labs/home/proxmox/korriban/guests/krb-app-01/services/web/service.yaml";

    /// <summary>A service of the web workflow: an OpenTofu stage, then an Ansible one.</summary>
    internal const string Service = """
        schemaVersion: 1
        workflow: web@1
        inputs: { port: 9000, hostname: web-01 }
        secrets:
          web_password: { store: web/password }
        """;

    /// <summary>A service of one OpenTofu stage, which holds no credentials.</summary>
    internal const string TofuOnly = "schemaVersion: 1\nworkflow: web-rules@1\ninputs: { port: 8080 }\n";

    /// <summary>
    /// The guest of <see cref="GuestConfigurationTests.Repository"/> with one service, and the workflows services can name:
    /// <c>web</c> (expose, deploy), <c>web-rules</c> (expose), <c>two-rules</c> (two OpenTofu stages) and <c>two-webs</c>
    /// (two Ansible stages).
    /// </summary>
    internal static TestRepository Repository(string service = Service, string guestPolicy = "", string webWorkflow = "")
    {
        var test = GuestConfigurationTests.Repository(guestPolicy);
        test.Write(ServiceFile, service);
        test.Write("modules/opentofu/web-rules/main.tf", """
            variable "port" {
              type = number
            }

            resource "terraform_data" "rule" {
              input = var.port
            }
            """);
        test.Write("modules/ansible/web/deploy.yml", "- hosts: all\n");
        test.Write("workflows/web/workflow.yaml", $$"""
            schemaVersion: 1
            version: "1.0"
            manages: service
            inputs: inputs.schema.json
            secrets: [web_password]
            stages:
              - name: expose
                executor: opentofu
                root: ../../modules/opentofu/web-rules
              - name: deploy
                executor: ansible
                root: ../../modules/ansible/web
                playbook: deploy.yml
                destructive: false
            {{webWorkflow}}
            """);
        test.Write("workflows/web/inputs.schema.json", """
            { "type": "object", "properties": { "inputs": { "type": "object", "properties": { "port": { "type": "integer" } }, "required": ["port"] } } }
            """);
        test.Write("workflows/web-rules/workflow.yaml", """
            schemaVersion: 1
            version: "1.0"
            manages: service
            stages:
              - { name: expose, executor: opentofu, root: ../../modules/opentofu/web-rules }
            """);
        test.Write("workflows/two-rules/workflow.yaml", """
            schemaVersion: 1
            version: "1.0"
            manages: service
            stages:
              - { name: one, executor: opentofu, root: ../../modules/opentofu/web-rules }
              - { name: two, executor: opentofu, root: ../../modules/opentofu/web-rules }
            """);
        test.Write("workflows/two-webs/workflow.yaml", """
            schemaVersion: 1
            version: "1.0"
            manages: service
            secrets: [web_password]
            stages:
              - { name: one, executor: ansible, root: ../../modules/ansible/web, playbook: deploy.yml }
              - { name: two, executor: ansible, root: ../../modules/ansible/web, playbook: deploy.yml }
            """);
        return test;
    }

    private static string Errors(TestRepository test) =>
        string.Join("\n", LabRepositoryLoader.Load(test.Root).Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message));

    private static void Replace(TestRepository test, string file, string from, string to)
    {
        var text = File.ReadAllText(Path.Combine(test.Root, file));
        Assert.Contains(from, text);
        test.Write(file, text.Replace(from, to));
    }

    [Fact]
    public void A_service_is_read_from_its_guests_folder_as_its_workflows_stages()
    {
        using var test = Repository();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        Assert.True(repository.IsValid(Address));
        var service = repository.Service(Address)!;
        Assert.Equal(Guest, service.GuestAddress);
        Assert.Equal("web@1.0", service.Workflow!.Label);
        Assert.Equal("web@1.0", repository.Resource(Address)!.Workflow);
        Assert.Equal(new[] { "expose", "deploy" }, service.Stages.Select(s => s.Name));
        Assert.Equal(new[] { Executors.OpenTofu, Executors.Ansible }, service.Stages.Select(s => s.Definition.Executor));
        Assert.All(service.Stages, s => Assert.Equal(9000, s.Inputs["port"]!.GetValue<int>()));
        Assert.Equal("web-01", service.Stages[1].Inputs["hostname"]!.GetValue<string>());
        // Only the Ansible stage gets the secrets; an OpenTofu stage holds no credentials.
        Assert.Empty(service.Stages[0].Secrets);
        Assert.Equal("web/password", service.Stages[1].Secrets["web_password"].Key);
        Assert.Equal("deploy", service.Stages[1].Definition.Configure!.OnChange!.Id);
        Assert.Contains(repository.StoreUses, u => u.Key == "web/password" && u.Consumer == Address);
    }

    [Theory]
    // Fields of later steps are refused, not ignored.
    [InlineData("schemaVersion: 1\nworkflow: web@1\nstages:\n  - { name: dns, interface: dns }\n")]
    [InlineData("schemaVersion: 1\nworkflow: web@1\nports: [8080]\n")]
    [InlineData("schemaVersion: 1\ninputs: { port: 1 }\n")]
    [InlineData("schemaVersion: 1\nworkflow: web\n")]
    [InlineData("schemaVersion: 1\nworkflow: web@1.0.0\n")]
    public void A_service_outside_the_schema_is_refused(string service)
    {
        using var test = Repository(service);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid(Address));
        Assert.Contains(repository.Issues, i => i.Address == Address && i.Severity == IssueSeverity.Error && i.File == ServiceFile);
        Assert.True(repository.IsValid(Guest));
    }

    [Fact]
    public void A_pipeline_yaml_is_the_earlier_format_and_says_where_the_new_one_is()
    {
        using var test = Repository();
        test.Delete(ServiceFile);
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/services/web/pipeline.yaml", "schemaVersion: 1\nstages: []\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid(Address));
        Assert.True(repository.IsValid(Guest));
        Assert.Contains(repository.IssuesFor(Address), i => i.Message.Contains("pipeline.yaml") && i.Message.Contains(LabRepositoryLoader.FormatDocument));
    }

    [Fact]
    public void A_labs_interfaces_are_refused_until_they_do_something()
    {
        using var test = Repository();
        Replace(test, "labs/home/lab.yaml", "name: Home", "name: Home\ninterfaces:\n  web: { workflow: web@1 }");
        Assert.False(LabRepositoryLoader.Load(test.Root).IsValid("home"));
    }

    [Fact]
    public void A_clusters_folder_is_not_read_and_says_so()
    {
        using var test = Repository();
        test.Write("labs/home/clusters/web/cluster.yaml", "members: []");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.True(repository.IsValid(Address));
        Assert.Contains(repository.Issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("Clusters are not supported yet"));
    }

    [Theory]
    [InlineData("workflow: nothing@1", "nothing@1 not found")]
    [InlineData("workflow: web@2", "web@2 not found; available 1.0")]
    [InlineData("workflow: app@1", "needs a workflow with manages: service")]
    [InlineData("workflow: web-rules@1\ninputs: { port: 1 }\nsecrets: { token: { store: web/token } }", "OpenTofu stages get no credentials")]
    [InlineData("workflow: web-rules@1\ninputs: { port: 1 }\nsecretFiles: { token: { store: web/token } }", "OpenTofu stages get no credentials")]
    [InlineData("workflow: web@1\ninputs: { port: 1 }", "needs secrets.web_password")]
    [InlineData("workflow: web@1\ninputs: { port: nope }\nsecrets: { web_password: { store: web/password } }", "port")]
    [InlineData("workflow: web@1\ninputs: { port: 1, web_password: x }\nsecrets: { web_password: { store: web/password } }", "set more than once")]
    [InlineData("workflow: web@1\ninputs: { port: 1 }\nsecrets: { web_password: { store: proxmox/korriban/privileged-token } }", "Proxmox API token")]
    public void A_service_is_checked_against_its_workflow_and_only_it_fails(string body, string expected)
    {
        using var test = Repository($"schemaVersion: 1\n{body}\n");
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/services/other/service.yaml", TofuOnly);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(expected, Errors(test));
        Assert.False(repository.IsValid(Address));
        Assert.True(repository.IsValid(Guest));
        Assert.True(repository.IsValid(Guest + "/other"));
    }

    [Fact]
    public void A_guest_cannot_name_a_service_workflow()
    {
        using var test = Repository();
        Replace(test, GuestFile, "workflow: app@1", "workflow: web@1");
        Assert.Contains("needs a workflow with manages: vm", Errors(test));
    }

    [Fact]
    public void A_service_has_one_approval_so_a_workflow_that_asks_for_another_is_refused()
    {
        using var test = Repository(webWorkflow: "outcomes:\n  busy:\n    approve:\n      vars: { force: true }");
        Assert.Contains("a service has one approval", Errors(test));
        Assert.False(LabRepositoryLoader.Load(test.Root).IsValid(Address));
    }

    [Theory]
    [InlineData("  user: ops\n", "needs access.user")]
    [InlineData("  hostKey: { store: access/app/host-key }\n", "needs access.hostKey")]
    public void An_ansible_stage_runs_on_the_guests_connection(string line, string expected)
    {
        using var test = Repository();
        Replace(test, GuestFile, line, "");
        Assert.Contains(expected, Errors(test));
    }

    /// <summary>The guest with a workflow that only makes the machine, so a service is all that runs on it, and with or without a key of its own.</summary>
    internal static TestRepository WithoutSetup(bool declaredKey = false, string service = Service)
    {
        var test = Repository(service);
        test.Write(GuestFile, MachineOnly(File.ReadAllText(Path.Combine(test.Root, GuestFile)), declaredKey));
        return test;
    }

    /// <summary>A guest's text from <see cref="GuestConfigurationTests.Repository"/>, on the workflow that only makes its machine.</summary>
    internal static string MachineOnly(string guest, bool declaredKey = false) =>
        guest[..guest.IndexOf("inputs:", StringComparison.Ordinal)].Replace("workflow: app@1", "workflow: test-vm@1")
            + "access:\n  user: ops\n" + (declaredKey ? "  sshKey: { store: access/app/ssh-key }\n" : "") + "  hostKey: { store: access/app/host-key }\n";

    private static string ProvisionHash(TestRepository test)
    {
        var repository = LabRepositoryLoader.Load(test.Root);
        return ProvisionInputs.Build(repository, repository.Guest(Guest)!, "c").Hash;
    }

    [Fact]
    public void A_guest_whose_only_ansible_is_a_service_stage_gets_octopushys_key()
    {
        using var test = WithoutSetup();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.DoesNotContain(repository.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.True(repository.IsValid(Address));
        Assert.True(ManagedAccess.Wanted(repository, repository.Guest(Guest)!, false));

        // The key reaches the machine through its provisioning, so the guest is provisioned again to carry it.
        using var tofuOnly = WithoutSetup(service: TofuOnly);
        var plain = LabRepositoryLoader.Load(tofuOnly.Root);
        Assert.False(ManagedAccess.Wanted(plain, plain.Guest(Guest)!, false));
        Assert.NotEqual(ProvisionHash(tofuOnly), ProvisionHash(test));
    }

    [Fact]
    public void Only_a_valid_service_gives_its_guest_octopushys_key_and_an_issued_key_stays_wanted()
    {
        // The workflow needs web_password, which this service does not give: the service is invalid, its guest is not.
        using var test = WithoutSetup(service: "schemaVersion: 1\nworkflow: web@1\ninputs: { port: 1 }\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid(Address));
        Assert.True(repository.IsValid(Guest));
        var guest = repository.Guest(Guest)!;
        Assert.False(ManagedAccess.Wanted(repository, guest, keyIssued: false));
        Assert.True(ManagedAccess.Wanted(repository, guest, keyIssued: true));

        using var tofuOnly = WithoutSetup(service: TofuOnly);
        var plain = LabRepositoryLoader.Load(tofuOnly.Root);
        Assert.Equal(ProvisionInputs.Build(plain, plain.Guest(Guest)!, "c").Hash, ProvisionInputs.Build(repository, guest, "c", keyIssued: false).Hash);

        // Once issued, it stays wanted whatever the services say, until the guest names a key of its own.
        Assert.True(ManagedAccess.Wanted(plain, plain.Guest(Guest)!, keyIssued: true));
        using var declared = WithoutSetup(declaredKey: true);
        var named = LabRepositoryLoader.Load(declared.Root);
        Assert.False(ManagedAccess.Wanted(named, named.Guest(Guest)!, keyIssued: true));
    }

    [Fact]
    public void A_key_the_guest_names_wins_over_octopushys()
    {
        using var test = WithoutSetup(declaredKey: true);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.True(repository.IsValid(Address));
        Assert.False(ManagedAccess.Wanted(repository, repository.Guest(Guest)!, false));

        // The same beside a setup stage, with a service whose stage runs on the guest.
        using var withSetup = Repository();
        var both = LabRepositoryLoader.Load(withSetup.Root);
        Assert.False(ManagedAccess.Wanted(both, both.Guest(Guest)!, false));
    }

    [Fact]
    public void An_error_in_guest_yaml_does_not_read_as_its_services_having_gone()
    {
        using var test = Repository();
        Replace(test, GuestFile, "vmid: 310", "vmid: nope");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.NotNull(repository.Resource(Address));
        Assert.False(repository.IsValid(Address));
    }

    [Fact]
    public void A_service_on_an_invalid_workflow_is_invalid_and_its_issues_show_on_it()
    {
        using var test = Repository();
        Replace(test, "workflows/web/workflow.yaml", "root: ../../modules/opentofu/web-rules", "root: ../../modules/opentofu/gone");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid(Address));
        Assert.Contains(repository.IssuesFor(Address), i => i.Address == "workflows/web");
    }

    private static string Hash(TestRepository test)
    {
        var repository = LabRepositoryLoader.Load(test.Root);
        return PipelineInputs.Build(repository, repository.Service(Address)!).Hash;
    }

    [Theory]
    [InlineData(ServiceFile, "port: 9000", "port: 9001")]
    [InlineData("workflows/web/workflow.yaml", "destructive: false", "destructive: true")]
    [InlineData("workflows/web/workflow.yaml", "version: \"1.0\"", "version: \"1.1\"")]
    [InlineData("workflows/web/inputs.schema.json", "integer", "number")]
    [InlineData("modules/ansible/web/deploy.yml", "- hosts: all", "- hosts: web")]
    [InlineData("modules/opentofu/web-rules/main.tf", "var.port", "var.port + 1")]
    [InlineData(GuestFile, "user: ops", "user: deploy")]
    public void The_hash_follows_what_a_run_is_built_from(string file, string from, string to)
    {
        using var test = Repository();
        var before = Hash(test);
        Replace(test, file, from, to);
        Assert.NotEqual(before, Hash(test));
    }

    [Fact]
    public void A_file_in_the_services_folder_changes_its_hash_and_another_module_does_not()
    {
        using var test = Repository();
        var before = Hash(test);
        test.Write("modules/ansible/app/site.yml", "- hosts: all\n# not the service's\n");
        test.Write("workflows/two-rules/notes.md", "another workflow\n");
        Assert.Equal(before, Hash(test));
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/services/web/app.conf", "listen 80");
        Assert.NotEqual(before, Hash(test));
    }

    [Fact]
    public void Policy_labels_a_description_and_how_the_reference_is_written_are_not_inputs()
    {
        using var test = Repository();
        var before = Hash(test);
        Replace(test, ServiceFile, "workflow: web@1", "description: A web server\npolicy: { approval: manual }\nexecution: { agentLabels: [web] }\nworkflow: web@1.0");
        Assert.Equal(before, Hash(test));
    }

    [Fact]
    public void A_guests_setup_and_a_sibling_are_not_inputs()
    {
        using var test = Repository();
        var before = Hash(test);
        Replace(test, GuestFile, "greeting: hello", "greeting: hi");
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/services/other/service.yaml", TofuOnly);
        Assert.Equal(before, Hash(test));
    }

    [Fact]
    public void A_service_does_not_configure_or_provision_its_guest_again()
    {
        using var test = Repository();
        var first = LabRepositoryLoader.Load(test.Root);
        var configure = ConfigureInputs.Build(first, first.Guest(Guest)!).Hash;
        var provision = ProvisionInputs.Build(first, first.Guest(Guest)!, "c").Hash;
        Replace(test, ServiceFile, "port: 9000", "port: 9001");
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/services/web/app.conf", "listen 80");
        Replace(test, "modules/ansible/web/deploy.yml", "- hosts: all", "- hosts: web");
        var second = LabRepositoryLoader.Load(test.Root);
        Assert.Equal(configure, ConfigureInputs.Build(second, second.Guest(Guest)!).Hash);
        Assert.Equal(provision, ProvisionInputs.Build(second, second.Guest(Guest)!, "c").Hash);
    }

    [Fact]
    public void With_no_policy_a_service_takes_what_is_configured_and_adds_only_its_override()
    {
        using var test = Repository();
        Replace(test, "octopushy.yaml", "schemaVersion: 1", "schemaVersion: 1\ndefaults:\n  policy: { approval: manual }");
        var repository = LabRepositoryLoader.Load(test.Root);
        var policy = PolicyResolver.For(repository, repository.Guest(Guest)!, repository.Service(Address)!);
        Assert.Equal("manual", policy.Approval.Value);
        Assert.Equal("octopushy.yaml", policy.Approval.SetBy);

        Replace(test, ServiceFile, "workflow:", "policy: { approval: automatic, destructive: manual }\nworkflow:");
        repository = LabRepositoryLoader.Load(test.Root);
        policy = PolicyResolver.For(repository, repository.Guest(Guest)!, repository.Service(Address)!);
        Assert.Equal("automatic", policy.Approval.Value);
        Assert.Equal(Address, policy.Approval.SetBy);
        Assert.Equal("manual", policy.Destructive.Value);
    }

    [Fact]
    public void Nothing_set_anywhere_means_a_service_deploys_automatically()
    {
        using var test = Repository();
        var repository = LabRepositoryLoader.Load(test.Root);
        var policy = PolicyResolver.For(repository, repository.Guest(Guest)!, repository.Service(Address)!);
        Assert.False(policy.ApprovalRequired);
        Assert.False(policy.DestructiveApprovalRequired);
        Assert.Equal("default", policy.Approval.SetBy);
    }

    [Fact]
    public void A_service_narrows_the_labels_its_guest_resolves_to()
    {
        using var test = Repository();
        Replace(test, ServiceFile, "workflow:", "execution: { agentLabels: [web] }\nworkflow:");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Equal(new[] { "web" }, PolicyResolver.AgentLabels(repository, repository.Guest(Guest)!, repository.Service(Address)!).Value);
    }

    [Fact]
    public void Only_the_stage_that_makes_a_vm_verifies_it()
    {
        using var test = Repository();
        Replace(test, "workflows/web/workflow.yaml", "root: ../../modules/opentofu/web-rules", "root: ../../modules/opentofu/web-rules\n    verify: none");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid(Address));
        Assert.Contains(repository.Issues, i => i.Address == "workflows/web" && i.Message.Contains("cannot verify"));
    }

    [Fact]
    public void A_service_workflow_offers_no_actions()
    {
        using var test = Repository(webWorkflow: "actions:\n  restart: { stage: deploy, playbook: deploy.yml, destructive: false }");
        Assert.Contains("offers Deploy only", Errors(test));
    }
}
