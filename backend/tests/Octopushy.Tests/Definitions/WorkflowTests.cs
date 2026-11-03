using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

/// <summary>The workflow format: reading and checking workflow.yaml, resolving references, mapping onto runs, and hashes.</summary>
public class WorkflowTests
{
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string Guest = "home/korriban/krb-app-01";
    private const string MachineWorkflow = "workflows/test-vm/workflow.yaml";

    private static void Replace(TestRepository test, string file, string from, string to)
    {
        var text = File.ReadAllText(Path.Combine(test.Root, file));
        Assert.Contains(from, text);
        test.Write(file, text.Replace(from, to));
    }

    private static List<DefinitionIssue> Errors(LabRepository repository) =>
        repository.Issues.Where(i => i.Severity == IssueSeverity.Error).ToList();

    private static string Messages(TestRepository test) =>
        string.Join("\n", Errors(LabRepositoryLoader.Load(test.Root)).Select(i => i.Message));

    [Theory]
    [InlineData("0.0", 0, 0)]
    [InlineData("2.3", 2, 3)]
    [InlineData("2.10", 2, 10)]
    public void A_version_is_major_dot_minor(string text, int major, int minor)
    {
        Assert.True(WorkflowVersion.TryParse(text, out var version));
        Assert.Equal(new WorkflowVersion(major, minor), version);
        Assert.Equal(text, version.ToString());
    }

    [Theory]
    [InlineData("2")]
    [InlineData("2.3.1")]
    [InlineData("02.3")]
    [InlineData("v2.3")]
    [InlineData("2.3\n")]
    [InlineData("-1.0")]
    public void Anything_else_is_not_a_version(string text) => Assert.False(WorkflowVersion.TryParse(text, out _));

    [Theory]
    [InlineData("fedora-vm@2", "fedora-vm", 2, null)]
    [InlineData("fedora-vm@2.3", "fedora-vm", 2, 3)]
    [InlineData("a@0.0", "a", 0, 0)]
    public void A_reference_names_a_major_or_an_exact_version(string text, string id, int major, int? minor)
    {
        Assert.Equal(new WorkflowReference(id, major, minor), WorkflowReference.Parse(text));
        Assert.Equal(text, WorkflowReference.Parse(text)!.ToString());
    }

    [Theory]
    [InlineData("fedora-vm")]
    [InlineData("fedora-vm@")]
    [InlineData("fedora-vm@v2")]
    [InlineData("fedora-vm@2.3.1")]
    [InlineData("Fedora@2")]
    public void A_reference_without_a_version_is_not_one(string text) => Assert.Null(WorkflowReference.Parse(text));

    [Theory]
    [InlineData("test-vm@1", true)]
    [InlineData("test-vm@1.4", true)]
    [InlineData("test-vm@1.3", false)]
    [InlineData("test-vm@2", false)]
    public void At_major_takes_any_minor_and_at_major_minor_only_that_one(string reference, bool resolves)
    {
        using var test = TestRepository.Minimal();
        Replace(test, MachineWorkflow, "version: \"1.0\"", "version: \"1.4\"");
        Replace(test, GuestFile, "workflow: test-vm@1", $"workflow: {reference}");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Equal(resolves, repository.IsValid(Guest));
        Assert.Equal(resolves ? "test-vm@1.4" : null, repository.Guest(Guest)!.Workflow?.Label);
        Assert.Equal(resolves ? "test-vm@1.4" : null, repository.Resource(Guest)!.Workflow);
        if (!resolves)
        {
            var issue = Assert.Single(Errors(repository));
            Assert.Equal($"{reference} not found; available 1.4.", issue.Message);
            Assert.Equal(GuestFile, issue.File);
            Assert.Equal(7, issue.Line);
        }
    }

    [Fact]
    public void A_reference_to_no_workflow_says_so_and_one_to_an_unreadable_workflow_points_at_it()
    {
        using var test = TestRepository.Minimal();
        Replace(test, GuestFile, "workflow: test-vm@1", "workflow: fedora-vm@3");
        Assert.Contains("fedora-vm@3 not found: there is no workflows/fedora-vm/workflow.yaml.", Messages(test));

        Replace(test, GuestFile, "workflow: fedora-vm@3", "workflow: test-vm@1");
        Replace(test, MachineWorkflow, "manages: vm", "manages: everything");
        var messages = Messages(test);
        Assert.Contains("test-vm@1 not found: workflows/test-vm/workflow.yaml could not be read", messages);
        Assert.Contains("manages", messages);
    }

    [Fact]
    public void A_bare_id_and_an_unquoted_version_get_a_plain_explanation()
    {
        using var test = TestRepository.Minimal();
        Replace(test, GuestFile, "workflow: test-vm@1", "workflow: test-vm");
        Replace(test, MachineWorkflow, "version: \"1.0\"", "version: 1.10");
        var messages = Messages(test);
        Assert.Contains("'test-vm' names no version. Write test-vm@<major>", messages);
        Assert.Contains("quote it, as in version: \"1.1\"", messages);
    }

    [Theory]
    [InlineData("stages:\n  - name: machine", "stages:\n  - name: setup\n    executor: ansible\n    root: ../../modules/opentofu/test-vm\n    playbook: main.tf\n  - name: machine", "starts with the OpenTofu stage")]
    [InlineData("    verify: proxmox\n", "    verify: proxmox\n  - { name: more, executor: opentofu, root: ../../modules/opentofu/test-vm }\n", "at most one stage after the machine's")]
    [InlineData("    verify: proxmox\n", "    verify: proxmox\n  - { name: machine, executor: opentofu, root: ../../modules/opentofu/test-vm }\n", "used more than once")]
    [InlineData("manages: vm", "manages: vm\nsecrets: [token]", "secrets are for an Ansible stage")]
    [InlineData("manages: vm", "manages: vm\noutcomes:\n  busy: { result: failed }", "outcomes are for an Ansible stage")]
    [InlineData("manages: vm", "manages: vm\nactions:\n  reboot: { stage: machine, playbook: main.tf, destructive: false }", "no Ansible stage of that name")]
    [InlineData("manages: vm", "manages: vm\ninputs: inputs.schema.json", "does not exist")]
    [InlineData("manages: vm", "manages: vm\nui: [not, an, object]", "ui")]
    [InlineData("manages: vm", "manages: vm\ngenerated: [vmid]", "generated")]
    public void A_workflow_is_checked_and_what_names_it_is_invalid(string from, string to, string expected)
    {
        using var test = TestRepository.Minimal();
        Replace(test, MachineWorkflow, from, to);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(Errors(repository), i => i.Message.Contains(expected) && i.Address == "workflows/test-vm");
        Assert.False(repository.IsValid(Guest));
        Assert.Contains(repository.IssuesFor(Guest), i => i.Address == "workflows/test-vm");
    }

    [Theory]
    [InlineData("root: ../../modules/opentofu/test-vm", "root: ../../../outside", "must be inside workflows/test-vm/ or modules/")]
    [InlineData("root: ../../modules/opentofu/test-vm", "root: /etc", "must be inside workflows/test-vm/ or modules/")]
    [InlineData("root: ../../modules/opentofu/test-vm", "root: ../other", "must be inside workflows/test-vm/ or modules/")]
    [InlineData("root: ../../modules/opentofu/test-vm", "root: ../../labs/home", "must be inside workflows/test-vm/ or modules/")]
    [InlineData("root: ../../modules/opentofu/test-vm", "root: ../../modules", "must be inside workflows/test-vm/ or modules/")]
    [InlineData("root: ../../modules/opentofu/test-vm", "root: ../../modules/opentofu/../../../escape", "must be inside workflows/test-vm/ or modules/")]
    [InlineData("manages: vm", "manages: vm\ninputs: ../../modules/opentofu/test-vm/schema.json", "must be inside workflows/test-vm/")]
    [InlineData("manages: vm", "manages: vm\ninputs: ../other/inputs.schema.json", "must be inside workflows/test-vm/")]
    public void A_path_outside_the_workflow_and_modules_is_refused(string from, string to, string expected)
    {
        using var test = TestRepository.Minimal();
        test.Write("workflows/other/inputs.schema.json", "{}");
        test.Write("workflows/other/main.tf", "# not this workflow's\n");
        test.Write("modules/opentofu/test-vm/schema.json", "{}");
        test.Write("labs/home/main.tf", "# not a module\n");
        Replace(test, MachineWorkflow, from, to);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(Errors(repository), i => i.Message.Contains(expected));
        Assert.False(repository.IsValid(Guest));
    }

    [Fact]
    public void A_root_through_a_symbolic_link_is_refused()
    {
        using var test = TestRepository.Minimal();
        var outside = Path.Combine(Path.GetTempPath(), "octopushy-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "main.tf"), "# from elsewhere\n");
            Directory.CreateSymbolicLink(Path.Combine(test.Root, "modules", "opentofu", "linked"), outside);
            Replace(test, MachineWorkflow, "root: ../../modules/opentofu/test-vm", "root: ../../modules/opentofu/linked");
            Assert.Contains("symbolic link modules/opentofu/linked", Messages(test));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void A_root_inside_the_workflows_own_folder_is_allowed()
    {
        using var test = TestRepository.Minimal();
        test.Write("workflows/test-vm/tofu/main.tf", "variable \"guest\" {\n  type = any\n}\n");
        Replace(test, MachineWorkflow, "root: ../../modules/opentofu/test-vm", "root: tofu");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        Assert.Equal("workflows/test-vm/tofu", repository.Guest(Guest)!.Workflow!.Machine!.Root);
    }

    [Fact]
    public void A_vm_workflow_maps_onto_a_provision_run_and_configure_runs()
    {
        using var test = GuestConfigurationTests.Repository(extraWorkflow: "outcomes:\n  busy: { result: succeeded }");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        var guest = repository.Guest(Guest)!;
        var workflow = guest.Workflow!;
        Assert.Equal(WorkflowKinds.Vm, workflow.Manages);

        var machine = workflow.Machine!;
        Assert.Equal(("machine", Executors.OpenTofu, "modules/opentofu/test-vm", "proxmox"), (machine.Name, machine.Executor, machine.Root, machine.Verify));
        Assert.Null(machine.Configure);
        var provision = ProvisionInputs.Build(repository, guest, "c");
        Assert.Same(machine, provision.Stage);
        Assert.Equal("app@1.0", provision.Variables["octopushy"]!["workflow"]!.GetValue<string>());
        Assert.Equal("hello", provision.Variables["inputs"]!["greeting"]!.GetValue<string>());

        var setup = guest.Setup!;
        Assert.Equal(("setup", Executors.Ansible, "modules/ansible/app"), (setup.Name, setup.Executor, setup.Root));
        Assert.Equal(new[] { WorkflowDefinition.SetupAction, "update-system" }, setup.Configure!.Actions.Keys);
        var onChange = setup.Configure.OnChange!;
        Assert.Equal((WorkflowDefinition.SetupAction, "site.yml", "prepare.yml", false, false), (onChange.Id, onChange.Playbook, onChange.Prepare, onChange.Destructive, onChange.UsesAppliedSnapshot));
        Assert.False(setup.Configure.Actions["update-system"].OnChange);
        Assert.Equal(["tailscale_key"], setup.Configure.Secrets);
        Assert.Equal("succeeded", setup.Configure.Outcomes["busy"].Result);
    }

    [Fact]
    public void A_vm_workflow_without_an_ansible_stage_has_no_setup()
    {
        using var test = TestRepository.Minimal();
        var repository = LabRepositoryLoader.Load(test.Root);
        var guest = repository.Guest(Guest)!;
        Assert.Null(guest.Setup);
        Assert.False(ManagedAccess.Wanted(repository, guest, false));
    }

    [Fact]
    public void A_service_workflow_maps_onto_one_pipeline_run()
    {
        using var test = ServiceDefinitionTests.Repository();
        var repository = LabRepositoryLoader.Load(test.Root);
        var service = repository.Service(ServiceDefinitionTests.Address)!;
        var inputs = PipelineInputs.Build(repository, service);
        Assert.Equal("web@1.0", inputs.Workflow.Label);
        Assert.Equal(new[] { ("expose", "none", false), ("deploy", "none", true) },
            inputs.Stages.Select(s => (s.Name, s.Definition.Verify, s.Definition.Configure is not null)));
        // A service's Ansible stage runs its playbook as an action named after the stage.
        Assert.Equal("deploy", Assert.Single(inputs.Stages[1].Definition.Configure!.Actions.Keys));
    }

    [Fact]
    public void The_ui_hints_are_kept_as_written()
    {
        using var test = TestRepository.Minimal();
        Replace(test, MachineWorkflow, "manages: vm", "manages: vm\nui:\n  order: [cores, memory]\n  cores: { widget: slider }");
        var workflow = LabRepositoryLoader.Load(test.Root).Workflows.Single();
        Assert.True(workflow.Valid);
        Assert.Equal("slider", workflow.Ui!["cores"]!["widget"]!.GetValue<string>());
    }

    private static string ProvisionHash(TestRepository test)
    {
        var repository = LabRepositoryLoader.Load(test.Root);
        return ProvisionInputs.Build(repository, repository.Guest(Guest)!, "c").Hash;
    }

    private static string ConfigureHash(TestRepository test)
    {
        var repository = LabRepositoryLoader.Load(test.Root);
        return ConfigureInputs.Build(repository, repository.Guest(Guest)!).Hash;
    }

    [Theory]
    [InlineData("version", true, true)]
    [InlineData("workflow folder", true, true)]
    [InlineData("machine module", true, false)]
    [InlineData("setup module", false, true)]
    [InlineData("unrelated module", false, false)]
    [InlineData("another workflow", false, false)]
    [InlineData("exact reference", false, false)]
    public void The_hashes_follow_the_resolved_workflow_and_the_roots_their_run_uses(string change, bool provision, bool configure)
    {
        using var test = GuestConfigurationTests.Repository();
        test.Write("modules/ansible/unrelated/site.yml", "- hosts: all\n");
        test.Write("workflows/test-vm/notes.md", "another workflow\n");
        var (provisionBefore, configureBefore) = (ProvisionHash(test), ConfigureHash(test));
        switch (change)
        {
            case "version":
                Replace(test, "workflows/app/workflow.yaml", "version: \"1.0\"", "version: \"1.1\"");
                break;
            case "workflow folder":
                test.Write("workflows/app/files/motd", "hello\n");
                break;
            case "machine module":
                test.Write("modules/opentofu/test-vm/extra.tf", "# changed\n");
                break;
            case "setup module":
                test.Write("modules/ansible/app/site.yml", "- hosts: all\n# changed\n");
                break;
            case "unrelated module":
                test.Write("modules/ansible/unrelated/site.yml", "- hosts: all\n# changed\n");
                break;
            case "another workflow":
                test.Write("workflows/test-vm/notes.md", "changed\n");
                break;
            case "exact reference":
                // What a guest is given is what its reference resolved to, not how the reference is written.
                Replace(test, GuestFile, "workflow: app@1", "workflow: app@1.0");
                break;
        }

        Assert.Equal(provision, provisionBefore != ProvisionHash(test));
        Assert.Equal(configure, configureBefore != ConfigureHash(test));
    }

    [Fact]
    public void A_recipes_folder_is_the_earlier_format_and_makes_the_repository_invalid()
    {
        using var test = TestRepository.Minimal();
        test.Write("recipes/test-vm/recipe.yaml", "schemaVersion: 1\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.RepositoryValid);
        Assert.Contains(Errors(repository), i => i.File == "recipes" && i.Message.Contains(LabRepositoryLoader.FormatDocument));
        Assert.All(repository.Resources, r => Assert.False(r.Valid));
    }

    [Fact]
    public void Workflows_resolve_through_their_source()
    {
        using var test = TestRepository.Minimal();
        var source = new LocalWorkflowSource(test.Root);
        Assert.Equal("test-vm@1.0", source.Resolve(new WorkflowReference("test-vm", 1, null)).Workflow!.Label);
        Assert.Equal("test-vm@1 not found; available 1.0.", source.Resolve(new WorkflowReference("test-vm", 1, 2)).Problem!.Replace("@1.2", "@1"));
        var repository = LabRepositoryLoader.Load(test.Root, source);
        Assert.True(repository.IsValid(Guest));
    }
}
