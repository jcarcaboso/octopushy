using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;
using Octopushy.Tests.Definitions;

namespace Octopushy.Tests.Server;

/// <summary>
/// Each run that logs in to a guest is pinned to exactly one host key source: the key a person set in access.hostKey, which
/// always wins, or for a container that names none, the fingerprints its create task logged. A VM never gets a container's
/// pin. These hold for every run that connects: a setup, a service's Ansible stage, and a rotation.
/// </summary>
public class TrustSourceTests
{
    private const string Guest = ServiceDefinitionTests.Guest;
    private const string GuestFile = ServiceDefinitionTests.GuestFile;
    private const string Manual = "access/app/host-key";

    /// <summary>The services fixture, its guest and its workflow made a container's or left a VM's, with or without access.hostKey.</summary>
    private static TestRepository Lab(string type, bool hostKey)
    {
        var test = ServiceDefinitionTests.Repository();
        var guest = File.ReadAllText(Path.Combine(test.Root, GuestFile)).Replace("type: vm", $"type: {type}");
        if (!hostKey)
            guest = guest.Replace("  hostKey: { store: access/app/host-key }\n", "");
        test.Write(GuestFile, guest);
        var workflow = Path.Combine(test.Root, "workflows/app/workflow.yaml");
        test.Write("workflows/app/workflow.yaml", File.ReadAllText(workflow).Replace("manages: vm", $"manages: {type}"));
        return test;
    }

    /// <summary>The host key binding of each run that connects, by run.</summary>
    private static Dictionary<string, Func<string>> Pins(TestRepository test, Guid id)
    {
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.True(repository.IsValid(Guest), string.Join("\n", repository.IssuesFor(Guest).Select(i => i.Message)));
        var factory = new RunFactory(Options.Create(new OctopushyOptions()), TimeProvider.System);
        var guest = repository.Guest(Guest)!;
        var resource = new Resource { Id = id, Address = Guest, Kind = ResourceKinds.Guest, Lab = "home" };
        var revision = new DefinitionRevision { Id = Guid.NewGuid(), Commit = new string('a', 40) };
        string Bound(Run run, string role) => Assert.Single(RunContext.Parse(run.Context).Secrets, s => RunContext.BareRole(s.Key) == role).Value;
        return new()
        {
            ["setup"] = () => Bound(factory.Configure(repository, guest, resource, revision, "setup", "manual", null, null), SecretNames.AnsibleHostKey),
            ["rotation"] = () => Bound(factory.RotateAccess(repository, guest, resource, revision, new AccessRotationContext(1, 2, [1])), SecretNames.AnsibleHostKey),
            ["service"] = () => Bound(factory.Pipeline(repository, repository.Service(ServiceDefinitionTests.Address)!,
                new Resource { Id = Guid.NewGuid(), Address = ServiceDefinitionTests.Address, Kind = ResourceKinds.Service, Lab = "home" },
                resource, revision, "manual"), SecretNames.AnsibleHostKey),
        };
    }

    [Fact]
    public void A_container_that_names_no_host_key_is_pinned_to_its_own_create_task_in_every_run()
    {
        using var test = Lab("container", hostKey: false);
        var id = Guid.NewGuid();
        Assert.All(Pins(test, id), pin => Assert.Equal(ContainerHostKeys.Key(id), pin.Value()));
    }

    [Fact]
    public void A_named_host_key_wins_for_a_container_in_every_run()
    {
        using var test = Lab("container", hostKey: true);
        Assert.All(Pins(test, Guid.NewGuid()), pin => Assert.Equal(Manual, pin.Value()));
    }

    [Fact]
    public void A_vm_names_its_host_key_and_never_gets_a_containers_pin()
    {
        using var test = Lab("vm", hostKey: true);
        Assert.All(Pins(test, Guid.NewGuid()), pin => Assert.Equal(Manual, pin.Value()));

        // A VM that names none is invalid here, and a run made for it anyway refuses rather than take another source.
        using var unnamed = Lab("vm", hostKey: false);
        var repository = LabRepositoryLoader.Load(unnamed.Root);
        Assert.False(repository.IsValid(Guest));
        Assert.False(ContainerHostKey.Pinned(repository.Guest(Guest)!));
        var factory = new RunFactory(Options.Create(new OctopushyOptions()), TimeProvider.System);
        var resource = new Resource { Id = Guid.NewGuid(), Address = Guest, Kind = ResourceKinds.Guest, Lab = "home" };
        var revision = new DefinitionRevision { Id = Guid.NewGuid(), Commit = new string('a', 40) };
        Assert.Contains("names no access.hostKey", Assert.Throws<RunException>(() =>
            factory.RotateAccess(repository, repository.Guest(Guest)!, resource, revision, new AccessRotationContext(1, 2, [1]))).Message);
    }

    [Fact]
    public void Only_a_container_without_a_named_key_reads_its_create_task()
    {
        foreach (var (type, hostKey, wanted) in new[] { ("container", false, true), ("container", true, false), ("vm", true, false) })
        {
            using var test = Lab(type, hostKey);
            var repository = LabRepositoryLoader.Load(test.Root);
            var guest = repository.Guest(Guest)!;
            Assert.Equal(wanted, ContainerHostKey.Wanted(repository, guest));
            var run = new RunFactory(Options.Create(new OctopushyOptions()), TimeProvider.System).Guest(repository, guest,
                new Resource { Id = Guid.NewGuid(), Address = Guest, Kind = ResourceKinds.Guest, Lab = "home" },
                new DefinitionRevision { Id = Guid.NewGuid(), Commit = new string('a', 40) }, "manual", false);
            Assert.Equal(wanted, RunContext.Parse(run.Context).ContainerHostKey);
        }
    }
}
