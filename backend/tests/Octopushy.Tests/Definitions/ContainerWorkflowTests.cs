using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

/// <summary>A workflow with <c>manages: container</c> and the guests of <c>type: container</c> that name it.</summary>
public class ContainerWorkflowTests
{
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string Guest = "home/korriban/krb-app-01";
    private const string MachineWorkflow = "workflows/test-vm/workflow.yaml";
    private const string AppWorkflow = "workflows/app/workflow.yaml";

    private static void Replace(TestRepository test, string file, string from, string to)
    {
        var text = File.ReadAllText(Path.Combine(test.Root, file));
        Assert.Contains(from, text);
        test.Write(file, text.Replace(from, to));
    }

    private static List<DefinitionIssue> Errors(LabRepository repository) =>
        repository.Issues.Where(i => i.Severity == IssueSeverity.Error).ToList();

    /// <summary>The minimal repository with its guest and its workflow made a container's.</summary>
    private static TestRepository Minimal()
    {
        var test = TestRepository.Minimal();
        Replace(test, GuestFile, "type: vm", "type: container");
        Replace(test, MachineWorkflow, "manages: vm", "manages: container");
        return test;
    }

    /// <summary>The configure fixture as a container whose setup logs in with Octopushy's key, naming no host key.</summary>
    private static TestRepository WithSetup(bool hostKey = false)
    {
        var test = GuestConfigurationTests.Repository();
        Replace(test, GuestFile, "type: vm", "type: container");
        if (!hostKey)
            Replace(test, GuestFile, "  hostKey: { store: access/app/host-key }\n", "");
        Replace(test, AppWorkflow, "manages: vm", "manages: container");
        return test;
    }

    [Fact]
    public void A_container_guest_of_a_container_workflow_is_valid_and_maps_onto_the_same_runs_as_a_vm()
    {
        using var test = Minimal();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        var guest = repository.Guest(Guest)!;
        Assert.Equal((GuestTypes.Container, "lxc"), (guest.Type, guest.ProxmoxKind));
        Assert.Equal(WorkflowKinds.Container, guest.Workflow!.Manages);
        var machine = guest.Workflow.Machine!;
        Assert.Equal(("machine", Executors.OpenTofu, "proxmox"), (machine.Name, machine.Executor, machine.Verify));
        Assert.Same(machine, ProvisionInputs.Build(repository, guest, "c").Stage);
    }

    [Fact]
    public void A_vm_keeps_its_kind()
    {
        using var test = TestRepository.Minimal();
        var guest = LabRepositoryLoader.Load(test.Root).Guest(Guest)!;
        Assert.Equal((GuestTypes.Vm, "qemu"), (guest.Type, guest.ProxmoxKind));
    }

    [Fact]
    public void A_container_naming_a_vm_workflow_is_invalid_and_says_which_to_change()
    {
        using var test = TestRepository.Minimal();
        Replace(test, GuestFile, "type: vm", "type: container");
        var repository = LabRepositoryLoader.Load(test.Root);
        var issue = Assert.Single(Errors(repository));
        Assert.Equal(GuestFile, issue.File);
        Assert.Equal("test-vm@1 resolved to test-vm@1.0, which has manages: vm, and a guest of type: container needs a workflow with manages: container. "
            + "Set type: vm to use it, or name a workflow that makes a container.", issue.Message);
        Assert.False(repository.IsValid(Guest));
    }

    [Fact]
    public void A_vm_naming_a_container_workflow_is_invalid_and_says_which_to_change()
    {
        using var test = TestRepository.Minimal();
        Replace(test, MachineWorkflow, "manages: vm", "manages: container");
        var repository = LabRepositoryLoader.Load(test.Root);
        var issue = Assert.Single(Errors(repository));
        Assert.Equal("test-vm@1 resolved to test-vm@1.0, which has manages: container, and a guest of type: vm needs a workflow with manages: vm. "
            + "Set type: container to use it, or name a workflow that makes a VM.", issue.Message);
    }

    [Fact]
    public void A_service_cannot_name_a_container_workflow_nor_a_guest_a_service_workflow()
    {
        using var test = Minimal();
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/services/web/service.yaml", "schemaVersion: 1\nworkflow: test-vm@1\n");
        var messages = string.Join("\n", Errors(LabRepositoryLoader.Load(test.Root)).Select(i => i.Message));
        Assert.Contains("which has manages: container, and a service needs a workflow with manages: service.", messages);
        Assert.DoesNotContain("Set type:", messages);
    }

    [Fact]
    public void An_unknown_guest_type_is_refused_by_the_schema()
    {
        using var test = TestRepository.Minimal();
        Replace(test, GuestFile, "type: vm", "type: lxc");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(Errors(repository), i => i.File == GuestFile);
        Assert.False(repository.IsValid(Guest));
    }

    [Theory]
    [InlineData("stages:\n  - name: machine", "stages:\n  - name: setup\n    executor: ansible\n    root: ../../modules/opentofu/test-vm\n    playbook: main.tf\n  - name: machine", "A workflow with manages: container starts with the OpenTofu stage")]
    [InlineData("    verify: proxmox\n", "    verify: proxmox\n  - { name: more, executor: opentofu, root: ../../modules/opentofu/test-vm }\n", "A workflow with manages: container has at most one stage after the machine's")]
    [InlineData("manages: container", "manages: container\nsecrets: [token]", "secrets are for an Ansible stage")]
    public void A_container_workflow_follows_the_rules_of_a_vm_workflow(string from, string to, string expected)
    {
        using var test = Minimal();
        Replace(test, MachineWorkflow, from, to);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(Errors(repository), i => i.Message.Contains(expected) && i.Address == "workflows/test-vm");
        Assert.False(repository.IsValid(Guest));
    }

    [Fact]
    public void Only_the_first_stage_of_a_container_workflow_verifies()
    {
        using var test = Minimal();
        Replace(test, MachineWorkflow, "    verify: proxmox\n", "    verify: proxmox\n  - { name: more, executor: opentofu, root: ../../modules/opentofu/test-vm, verify: proxmox }\n");
        var messages = string.Join("\n", Errors(LabRepositoryLoader.Load(test.Root)).Select(i => i.Message));
        Assert.Contains("only the OpenTofu stage that makes a machine, the first of a workflow with manages: vm or manages: container, does", messages);
    }

    [Fact]
    public void A_container_whose_setup_names_no_host_key_is_pinned_from_its_create_task()
    {
        using var test = WithSetup();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        var guest = repository.Guest(Guest)!;
        Assert.True(ContainerHostKey.Pinned(guest));
        Assert.True(ContainerHostKey.Wanted(repository, guest));
        var inputs = ConfigureInputs.Build(repository, guest);
        Assert.Null(inputs.HostKey);
        Assert.Equal(WorkflowDefinition.SetupAction, inputs.Configure.OnChange!.Id);
    }

    [Fact]
    public void A_named_host_key_wins()
    {
        using var test = WithSetup(hostKey: true);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        var guest = repository.Guest(Guest)!;
        Assert.False(ContainerHostKey.Wanted(repository, guest));
        Assert.Equal("access/app/host-key", ConfigureInputs.Build(repository, guest).HostKey!.Key);
    }

    [Fact]
    public void A_container_without_ansible_work_reads_no_host_keys_and_a_vm_still_names_its_own()
    {
        using var bare = Minimal();
        var repository = LabRepositoryLoader.Load(bare.Root);
        Assert.False(ContainerHostKey.Wanted(repository, repository.Guest(Guest)!));

        using var vm = GuestConfigurationTests.Repository();
        Replace(vm, GuestFile, "  hostKey: { store: access/app/host-key }\n", "");
        Assert.Contains(Errors(LabRepositoryLoader.Load(vm.Root)), i => i.Message.Contains("so it needs access.hostKey"));
    }
}
