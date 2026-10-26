using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

public class ManagedWorkerDefinitionTests
{
    public const string GuestPath = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    public const string Generation = "884d09b4-0934-43a4-bffb-d2f7977bc01c";

    [Fact]
    public void A_worker_is_a_normal_guest_with_a_versioned_identity_and_two_disks()
    {
        using var test = Repository();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        Assert.Single(repository.Guests);
    }

    [Theory]
    [InlineData("templateVersion: 1", "templateVersion: 2")]
    [InlineData(Generation, "not-a-generation")]
    [InlineData("maxConcurrency: 1", "maxConcurrency: 0")]
    [InlineData("labels: [home]", "labels: [home, home]")]
    [InlineData("type: vm", "type: vm\nlifecycle: retired")]
    [InlineData("approval: manual", "approval: automatic")]
    [InlineData("template: octopushy-worker-v1", "template: other")]
    [InlineData("disks: [{ size: 8 }, { size: 16 }]", "disks: [{ size: 8 }]")]
    [InlineData("maxConcurrency: 1", "maxConcurrency: 1, token: bad")]
    public void Unsafe_or_incomplete_worker_definitions_are_rejected(string before, string after)
    {
        using var test = Repository();
        test.Write(GuestPath, File.ReadAllText(Path.Combine(test.Root, GuestPath)).Replace(before, after));
        Assert.Contains(LabRepositoryLoader.Load(test.Root).Issues, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Two_guests_cannot_claim_one_generation()
    {
        using var test = Repository();
        test.Write(GuestPath.Replace("krb-app-01", "worker-2"),
            File.ReadAllText(Path.Combine(test.Root, GuestPath)).Replace("vmid: 310", "vmid: 311"));
        var repo = LabRepositoryLoader.Load(test.Root);
        Assert.All(repo.Guests, g => Assert.False(repo.IsValid(g.Address)));
    }

    public static TestRepository Repository()
    {
        var test = TestRepository.Minimal();
        test.Write("recipes/octopushy-worker-v1/recipe.yaml", """
            schemaVersion: 1
            operation: provision
            executor: opentofu
            root: ../../modules/opentofu/test-vm
            verify: proxmox
            guestFilesystem: nixos-ext4-root
            """);
        test.Write("templates/octopushy-worker-v1/template.yaml", $$"""
            schemaVersion: 1
            os: nixos
            source:
              url: https://images.example.invalid/worker.qcow2
              checksum: sha256:{{new string('a', 64)}}
            """);
        test.Write(GuestPath, $$"""
            schemaVersion: 1
            type: vm
            host: korriban
            vmid: 310
            template: octopushy-worker-v1
            hardware: { sockets: 1, cores: 2, memory: 2048, disks: [{ size: 8 }, { size: 16 }] }
            network: { bridge: vmbr0, mode: dhcp }
            provision:
              recipe: octopushy-worker-v1
              state: { backend: postgres }
            policy: { approval: manual, destructive: manual }
            worker: { templateVersion: 1, generation: {{Generation}}, labels: [home], maxConcurrency: 1 }
            """);
        return test;
    }
}
