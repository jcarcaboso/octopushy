using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

/// <summary>A guest declared <c>machine: attached</c>: a machine Octopushy did not create, so it brings its own keys.</summary>
public class AttachedGuestDefinitionTests
{
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string Guest = "home/korriban/krb-app-01";

    private static void Replace(TestRepository test, string file, string from, string to)
    {
        var text = File.ReadAllText(Path.Combine(test.Root, file));
        Assert.Contains(from, text);
        test.Write(file, text.Replace(from, to));
    }

    private static List<DefinitionIssue> Errors(LabRepository repository) =>
        repository.Issues.Where(i => i.Severity == IssueSeverity.Error).ToList();

    [Fact]
    public void An_attached_guest_with_its_own_keys_is_valid_and_gets_no_key_of_octopushys()
    {
        using var test = GuestConfigurationTests.Repository();
        Replace(test, GuestFile, "type: vm\n", "type: vm\nmachine: attached\n");
        var repository = LabRepositoryLoader.Load(test.Root);

        Assert.Empty(Errors(repository));
        var guest = repository.Guest(Guest)!;
        Assert.True(guest.Attached);
        Assert.False(ManagedAccess.Wanted(repository, guest, keyIssued: true));
        Assert.Equal(HostKeyTrust.Declared, ManagedHostKey.TrustOf(repository, guest));
    }

    [Fact]
    public void An_attached_guest_needs_its_own_ssh_key_and_host_key_for_ansible_work()
    {
        using var test = GuestConfigurationTests.Repository();
        Replace(test, GuestFile, "type: vm\n", "type: vm\nmachine: attached\n");
        Replace(test, GuestFile, "  sshKey: { store: access/app/ssh-key }\n", "");
        Replace(test, GuestFile, "  hostKey: { store: access/app/host-key }\n", "");
        var errors = Errors(LabRepositoryLoader.Load(test.Root));

        Assert.Contains(errors, i => i.Message.Contains("an attached machine has no key of Octopushy's: set access.sshKey"));
        Assert.Contains(errors, i => i.Message.Contains("so it needs access.hostKey"));
    }

    [Fact]
    public void Machine_is_managed_or_attached()
    {
        using var test = TestRepository.Minimal();
        Replace(test, GuestFile, "type: vm\n", "type: vm\nmachine: adopted\n");
        Assert.Contains(Errors(LabRepositoryLoader.Load(test.Root)), i => i.Address == Guest);

        using var managed = TestRepository.Minimal();
        Replace(managed, GuestFile, "type: vm\n", "type: vm\nmachine: managed\n");
        var repository = LabRepositoryLoader.Load(managed.Root);
        Assert.Empty(Errors(repository));
        Assert.False(repository.Guest(Guest)!.Attached);
    }
}
