using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

/// <summary>
/// A grant of a protected secret covers how its code runs, and the guest it creates is part of
/// that: whoever can log in to it, where it is, what it mounts, and which credentials reach
/// the Proxmox API it is created through.
/// </summary>
public class ReleaseBindingTests
{
    private const string InstallationFile = "labs/home/proxmox/moraband/installation.yaml";

    private static string Digest(TestRepository test)
    {
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.True(repository.IsValid(ClusterDefinitionTests.Secondary), string.Join("; ", repository.Issues.Select(i => i.Message)));
        return ReleaseBinding.Of(repository, repository.Guest(ClusterDefinitionTests.Secondary)!).Digest;
    }

    private static string Edited(TestRepository test, string file, string from, string to)
    {
        var path = Path.Combine(test.Root, file);
        var text = File.ReadAllText(path);
        Assert.Contains(from, text);
        return text.Replace(from, to);
    }

    [Fact]
    public void A_login_key_changes_what_the_grant_covers()
    {
        // The attack: a commit lets an attacker's key into a machine that is handed the password.
        using var test = ClusterDefinitionTests.Repository();
        var before = Digest(test);
        test.Write(ClusterDefinitionTests.SecondaryFile, ClusterDefinitionTests.Guest("moraband", 420, "10.1.0.42",
            "access: { sshAuthorizedKeys: [\"ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAttackerKeyAttackerKeyAttackerKeyAttackerKey attacker\"] }"));
        Assert.NotEqual(before, Digest(test));
    }

    [Fact]
    public void A_login_key_is_shown_to_the_operator()
    {
        using var test = ClusterDefinitionTests.Repository();
        test.Write(ClusterDefinitionTests.SecondaryFile, ClusterDefinitionTests.Guest("moraband", 420, "10.1.0.42",
            "access: { sshAuthorizedKeys: [\"ssh-ed25519 AAAAC3Nza attacker\"] }"));
        var repository = LabRepositoryLoader.Load(test.Root);
        var execution = ReleaseBinding.Of(repository, repository.Guest(ClusterDefinitionTests.Secondary)!).Execution;
        Assert.Equal("ssh-ed25519 AAAAC3Nza attacker", execution["guest"]!["access"]!["sshAuthorizedKeys"]![0]!.GetValue<string>());
    }

    [Fact]
    public void The_proxmox_credentials_a_guest_is_created_with_are_covered()
    {
        // The attack: the password is sent to Proxmox as the API token by swapping a store key.
        using var test = ClusterDefinitionTests.Repository();
        var before = Digest(test);
        test.Write(InstallationFile, Edited(test, InstallationFile, "readOnlyToken: { store: proxmox/korriban/read-only-token }", "readOnlyToken: { store: dns/admin }"));
        var readOnly = Digest(test);
        Assert.NotEqual(before, readOnly);
        test.Write(InstallationFile, Edited(test, InstallationFile, "privilegedToken: { store: proxmox/korriban/privileged-token }", "privilegedToken: { store: proxmox/moraband/privileged-token }"));
        var privileged = Digest(test);
        Assert.NotEqual(readOnly, privileged);
        test.Write(InstallationFile, Edited(test, InstallationFile, "tls: { verify: false }", "tls: { verify: false, caCertificate: { store: proxmox/ca } }"));
        Assert.NotEqual(privileged, Digest(test));
    }

    [Fact]
    public void A_pinned_version_of_a_credential_is_covered()
    {
        using var test = ClusterDefinitionTests.Repository();
        var before = Digest(test);
        test.Write(InstallationFile, Edited(test, InstallationFile, "readOnlyToken: { store: proxmox/korriban/read-only-token }", "readOnlyToken: { store: proxmox/korriban/read-only-token, version: 1 }"));
        Assert.NotEqual(before, Digest(test));
    }

    [Theory]
    [InlineData("vmid: 420", "vmid: 421")]
    [InlineData("hardware: { cores: 1, memory: 512 }", "hardware: { cores: 1, memory: 512, mountPoints: [{ path: /etc/dns, volume: \"local-lvm:vm-9320-disk-0\" }] }")]
    [InlineData("hardware: { cores: 1, memory: 512 }", "hardware: { cores: 2, memory: 512 }")]
    [InlineData("type: container", "type: container\nprofiles: [dns]")]
    public void Where_the_guest_is_and_what_it_mounts_is_covered(string from, string to)
    {
        using var test = ClusterDefinitionTests.Repository();
        var before = Digest(test);
        test.Write(ClusterDefinitionTests.SecondaryFile, Edited(test, ClusterDefinitionTests.SecondaryFile, from, to));
        Assert.NotEqual(before, Digest(test));
    }

    [Theory]
    [InlineData("datastores: { images: local, disks: local-lvm }", "datastores: { images: local, disks: other }")]
    [InlineData("bridges: [vmbr0]", "bridges: [vmbr0, vmbr1]")]
    public void The_hosts_datastores_and_bridges_are_covered(string from, string to)
    {
        using var test = ClusterDefinitionTests.Repository();
        var before = Digest(test);
        test.Write(InstallationFile, Edited(test, InstallationFile, from, to));
        Assert.NotEqual(before, Digest(test));
    }

    [Fact]
    public void What_cannot_change_the_guest_is_not_covered()
    {
        using var test = ClusterDefinitionTests.Repository();
        var before = Digest(test);
        test.Write(ClusterDefinitionTests.SecondaryFile, ClusterDefinitionTests.Guest("moraband", 420, "10.1.0.42",
            "description: a DNS server\npolicy: { approval: manual }\nexecution: { agentLabels: [home] }\nsetup: { diskReporter: { enabled: false } }"));
        Assert.Equal(before, Digest(test));
    }
}
