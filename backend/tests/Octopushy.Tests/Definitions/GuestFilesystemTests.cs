using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Definitions;

public class GuestFilesystemTests
{
    [Fact]
    public void An_opted_in_guest_has_separate_growth_and_capacity_steps_but_retirement_does_not()
    {
        using var test = Repository();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        var guest = Assert.Single(repository.Guests);
        var factory = new RunFactory(Options.Create(new OctopushyOptions()), TimeProvider.System);
        Run Build(GuestDefinition g) => factory.Guest(repository, g,
            new Resource { Kind = ResourceKinds.Guest, Address = g.Address, Lab = g.Lab },
            new DefinitionRevision { Commit = "rev" }, "test");
        Assert.Equal(new[] { "plan", "approval", "apply", "verify", "grow-root", "verify-root" },
            Build(guest).Steps.Select(s => s.Name));
        Assert.DoesNotContain(Build(guest with { Lifecycle = "retired" }).Steps,
            s => s.Name is "grow-root" or "verify-root");
    }

    [Theory]
    [InlineData("unknown", "proxmox", 26)]
    [InlineData("nixos-ext4-root", "none", 26)]
    [InlineData("nixos-ext4-root", "proxmox", 0)]
    public void Unsupported_or_incomplete_growth_contracts_are_rejected(string mode, string verify, int size)
    {
        using var test = Repository(mode, verify, size);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(repository.Issues, i => i.Severity == IssueSeverity.Error);
    }

    public static TestRepository Repository(string mode = "nixos-ext4-root", string verify = "proxmox", int size = 26)
    {
        var test = TestRepository.Minimal();
        const string recipe = "recipes/test-vm/recipe.yaml";
        test.Write(recipe, File.ReadAllText(Path.Combine(test.Root, recipe))
            .Replace("verify: proxmox", $"verify: {verify}") + $"\nguestFilesystem: {mode}\n");
        const string guest = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
        test.Write(guest, File.ReadAllText(Path.Combine(test.Root, guest))
            .Replace("memory: 2048", $"memory: 2048, disks: [{{ size: {size} }}]"));
        return test;
    }
}
