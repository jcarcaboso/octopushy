using Octopushy.Definitions;
using Octopushy.Tests.Server;

namespace Octopushy.Tests.Definitions;

/// <summary>Which guests get a host key of Octopushy's, and what a workflow and a definition must say for that.</summary>
public class ManagedHostKeyDefinitionTests
{
    private const string Address = "home/korriban/krb-app-01";
    private const string InstallationFile = "labs/home/proxmox/korriban/installation.yaml";
    private const string WorkflowFile = "workflows/app/workflow.yaml";

    private static string Errors(TestRepository test) =>
        string.Join("\n", LabRepositoryLoader.Load(test.Root).Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message));

    private static void Edit(TestRepository test, string file, string from, string to)
    {
        var text = File.ReadAllText(Path.Combine(test.Root, file));
        Assert.Contains(from, text);
        test.Write(file, text.Replace(from, to));
    }

    [Fact]
    public void A_guest_without_a_pin_of_its_own_gets_octopushys_and_the_upload_settings()
    {
        using var test = ManagedHostKeyTests.Managed();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        var guest = repository.Guest(Address)!;
        Assert.True(guest.Workflow!.Machine!.ManagedHostKey);
        Assert.True(ManagedHostKey.Wanted(repository, guest, keyIssued: false));
        Assert.Null(ConfigureInputs.Build(repository, guest).HostKey);
        var settings = ManagedHostKey.Settings(repository.Installation(guest.InstallationAddress)!)!;
        Assert.Equal("local", settings.Datastore);
        Assert.Equal("octopushy-snippets", settings.User);
        Assert.Equal(new SnippetHost("10.1.0.10", 22, new StoreReference("proxmox/korriban/ssh-host-key")), settings.Hosts["korriban"]);
        var variables = ProvisionInputs.Build(repository, guest, "c").Variables["octopushy_snippets"]!;
        Assert.Equal("local", variables["datastore"]!.GetValue<string>());
        Assert.Equal("korriban", variables["node"]!["name"]!.GetValue<string>());
        Assert.Equal("10.1.0.10", variables["node"]!["address"]!.GetValue<string>());
        // The public half joins it when a run is built, so it is never in Git or in a hash.
        Assert.Null(variables["hostPublicKey"]);
    }

    [Fact]
    public void The_datastore_user_and_address_can_be_set()
    {
        using var test = ManagedHostKeyTests.Managed();
        Edit(test, InstallationFile, "snippets:\n", "snippets:\n  datastore: snippets-dir\n  user: uploader\n");
        Edit(test, InstallationFile, "korriban: { hostKey:", "korriban: { address: 10.1.5.10, port: 2222, hostKey:");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        var settings = ManagedHostKey.Settings(repository.Installation("home/korriban")!)!;
        Assert.Equal(("snippets-dir", "uploader"), (settings.Datastore, settings.User));
        Assert.Equal(("10.1.5.10", 2222), (settings.Hosts["korriban"].Address, settings.Hosts["korriban"].Port));
    }

    [Fact]
    public void A_guest_needs_a_pin_unless_its_workflow_delivers_one_to_a_host_set_up_for_it()
    {
        using var test = ManagedHostKeyTests.Managed();
        Edit(test, WorkflowFile, "    hostKey: managed\n", "");
        Assert.Contains("needs access.hostKey, unless its machine stage has hostKey: managed and its installation lists host korriban under snippets.hosts",
            Errors(test));

        using var noHost = ManagedHostKeyTests.Managed();
        Edit(noHost, InstallationFile, "korriban: { hostKey:", "other: { hostKey:");
        var errors = Errors(noHost);
        Assert.Contains("lists host korriban under snippets.hosts", errors);
        Assert.Contains("snippets.hosts.other is not a host of this installation", errors);
    }

    [Theory]
    [InlineData("    playbook: site.yml\n", "    playbook: site.yml\n    hostKey: managed\n")]
    [InlineData("    hostKey: managed\n", "    hostKey: always\n")]
    public void Only_the_stage_that_makes_the_vm_can_install_a_managed_key(string from, string to)
    {
        using var test = ManagedHostKeyTests.Managed();
        Edit(test, WorkflowFile, from, to);
        Assert.NotEmpty(Errors(test));
    }

    [Fact]
    public void A_service_workflow_cannot_install_a_managed_key()
    {
        using var test = ServiceDefinitionTests.Repository();
        Edit(test, "workflows/web/workflow.yaml", "    root: ../../modules/opentofu/web-rules\n",
            "    root: ../../modules/opentofu/web-rules\n    hostKey: managed\n");
        Assert.Contains("only the OpenTofu stage that makes a VM, the first of a workflow with manages: vm, does", Errors(test));
    }

    [Fact]
    public void The_hosts_pin_is_not_the_api_token()
    {
        using var token = ManagedHostKeyTests.Managed();
        Edit(token, InstallationFile, "store: proxmox/korriban/ssh-host-key", "store: proxmox/korriban/privileged-token");
        Assert.Contains("cannot be the installation's API token", Errors(token));
    }

    [Fact]
    public void A_guest_with_its_own_pin_keeps_its_provisioning_hash_when_the_installation_is_set_up()
    {
        using var before = GuestConfigurationTests.Repository();
        // The workflow is hashed, so a workflow that delivers keys moves every hash, as any new version does; the
        // installation's block does not.
        using var after = GuestConfigurationTests.Repository();
        Edit(after, InstallationFile, "limits: { concurrentOperations: 2 }\n",
            "limits: { concurrentOperations: 2 }\nsnippets:\n  hosts:\n    korriban: { hostKey: { store: proxmox/korriban/ssh-host-key } }\n");
        var old = LabRepositoryLoader.Load(before.Root);
        var updated = LabRepositoryLoader.Load(after.Root);
        Assert.Empty(updated.Issues);
        Assert.Equal(ProvisionInputs.Build(old, old.Guest(Address)!, "c").Hash, ProvisionInputs.Build(updated, updated.Guest(Address)!, "c").Hash);
        Assert.False(ManagedHostKey.Wanted(updated, updated.Guest(Address)!, keyIssued: false));
        Assert.Null(ProvisionInputs.Build(updated, updated.Guest(Address)!, "c").Variables["octopushy_snippets"]);
    }

    [Fact]
    public void A_guest_that_keeps_its_own_pin_on_a_workflow_that_delivers_one_plans_nothing_new()
    {
        using var test = ManagedHostKeyTests.Managed(declaredPin: true);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        var guest = repository.Guest(Address)!;
        Assert.False(ManagedHostKey.Wanted(repository, guest, keyIssued: false));
        Assert.Null(ProvisionInputs.Build(repository, guest, "c").Variables["octopushy_snippets"]);
        Assert.Equal("access/app/host-key", ConfigureInputs.Build(repository, guest).HostKey!.Key);
    }

    [Fact]
    public void Once_issued_the_key_stays_while_offered_whatever_the_guests_ansible_work()
    {
        using var test = ManagedHostKeyTests.Managed();
        var repository = LabRepositoryLoader.Load(test.Root);
        var guest = repository.Guest(Address)!;
        var machineOnly = guest with { Workflow = guest.Workflow! with { Stages = [guest.Workflow.Machine!] } };
        // No Ansible work: no key to start with, but one already issued is kept, or the VM would be replaced.
        Assert.False(ManagedHostKey.Wanted(repository, machineOnly, keyIssued: false));
        Assert.True(ManagedHostKey.Wanted(repository, machineOnly, keyIssued: true));
        Assert.NotNull(ProvisionInputs.Build(repository, machineOnly, "c", hostKeyIssued: true).Variables["octopushy_snippets"]);
        Assert.NotEqual(ProvisionInputs.Build(repository, machineOnly, "c").Hash, ProvisionInputs.Build(repository, machineOnly, "c", hostKeyIssued: true).Hash);
        // A pin of its own always wins.
        var json = guest.Json.DeepClone().AsObject();
        json["access"]!["hostKey"] = new System.Text.Json.Nodes.JsonObject { ["store"] = "access/app/host-key" };
        Assert.False(ManagedHostKey.Wanted(repository, guest with { Json = json }, keyIssued: true));
    }

    [Fact]
    public void A_service_stage_on_the_guest_needs_a_pin_unless_the_guest_gets_octopushys()
    {
        using var managed = ManagedHostKeyTests.Managed(start: ServiceDefinitionTests.Repository());
        var repository = LabRepositoryLoader.Load(managed.Root);
        Assert.DoesNotContain(repository.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.True(ManagedHostKey.Wanted(repository, repository.Guest(Address)!, keyIssued: false));

        using var plain = ManagedHostKeyTests.Managed(start: ServiceDefinitionTests.Repository());
        Edit(plain, WorkflowFile, "    hostKey: managed\n", "");
        Assert.Contains("Stage 'deploy' runs on the guest, which needs access.hostKey, unless the guest's machine stage has hostKey: managed", Errors(plain));
    }
}
