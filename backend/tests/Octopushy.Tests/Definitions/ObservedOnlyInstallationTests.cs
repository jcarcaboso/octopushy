using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

/// <summary><c>manage: false</c>: an installation Octopushy observes and never changes. See docs/safety-boundary.md.</summary>
public class ObservedOnlyInstallationTests
{
    private const string Installation = "labs/home/proxmox/korriban/installation.yaml";

    internal static void ObserveOnly(TestRepository test)
    {
        var path = Path.Combine(test.Root, Installation);
        test.Write(Installation, File.ReadAllText(path).Replace("type: standalone", "manage: false\ntype: standalone"));
    }

    [Fact]
    public void An_installation_is_managed_unless_it_says_otherwise()
    {
        using var test = TestRepository.Minimal();
        Assert.True(LabRepositoryLoader.Load(test.Root).Installation("home/korriban")!.Managed);

        var path = Path.Combine(test.Root, Installation);
        test.Write(Installation, File.ReadAllText(path).Replace("type: standalone", "manage: true\ntype: standalone"));
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        Assert.True(repository.Installation("home/korriban")!.Managed);
    }

    [Fact]
    public void An_observed_only_installation_on_its_own_is_valid()
    {
        using var test = TestRepository.Minimal();
        test.Delete("labs/home/proxmox/korriban/guests");
        ObserveOnly(test);

        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        Assert.False(repository.Installation("home/korriban")!.Managed);
        Assert.True(repository.IsValid("home/korriban"));
    }

    [Fact]
    public void A_guest_under_an_observed_only_installation_is_invalid()
    {
        using var test = TestRepository.Minimal();
        ObserveOnly(test);

        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid("home/korriban/krb-app-01"));
        // The installation itself stays valid, so it is still observed.
        Assert.True(repository.IsValid("home/korriban"));
        var issue = Assert.Single(repository.IssuesFor("home/korriban/krb-app-01"), i => i.Severity == IssueSeverity.Error);
        Assert.Contains("home/korriban is observed only (manage: false", issue.Message);
        Assert.Contains("no guest may be defined under it", issue.Message);
    }

    [Fact]
    public void A_service_under_an_observed_only_installation_is_invalid()
    {
        using var test = ServiceDefinitionTests.Repository();
        ObserveOnly(test);

        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid(ServiceDefinitionTests.Address));
        Assert.Contains(repository.IssuesFor(ServiceDefinitionTests.Address),
            i => i.Address == ServiceDefinitionTests.Address && i.Message.Contains("no service may be defined under it"));
    }

    [Fact]
    public void Manage_must_be_a_boolean()
    {
        using var test = TestRepository.Minimal();
        var path = Path.Combine(test.Root, Installation);
        test.Write(Installation, File.ReadAllText(path).Replace("type: standalone", "manage: sometimes\ntype: standalone"));

        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid("home/korriban"));
        Assert.Contains(repository.Issues, i => i.Severity == IssueSeverity.Error && i.File == Installation);
    }
}
