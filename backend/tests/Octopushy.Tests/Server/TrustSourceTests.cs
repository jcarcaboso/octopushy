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

    /// <summary>#43's managed VM fixture, with services, with or without a declared access.hostKey.</summary>
    private static TestRepository ManagedVm(bool declared) =>
        ManagedHostKeyTests.Managed(declaredPin: declared, start: ServiceDefinitionTests.Repository());

    /// <summary>What each run that logs in pins: the store key it binds, if any, and whether it pins the managed key.</summary>
    private static Dictionary<string, (string? Bound, bool Managed)> Sources(TestRepository test, Guid id)
    {
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.True(repository.IsValid(Guest), string.Join("\n", repository.IssuesFor(Guest).Select(i => i.Message)));
        var factory = new RunFactory(Options.Create(new OctopushyOptions()), TimeProvider.System);
        var guest = repository.Guest(Guest)!;
        var resource = new Resource { Id = id, Address = Guest, Kind = ResourceKinds.Guest, Lab = "home" };
        var revision = new DefinitionRevision { Id = Guid.NewGuid(), Commit = new string('a', 40) };
        (string?, bool) Of(Run run)
        {
            var context = RunContext.Parse(run.Context);
            var bound = context.Secrets.Where(s => RunContext.BareRole(s.Key) == SecretNames.AnsibleHostKey).Select(s => s.Value).Distinct().ToList();
            Assert.True(bound.Count <= 1, "a run binds one pin at most");
            return (bound.SingleOrDefault(), context.ManagedHostKey);
        }
        return new()
        {
            ["setup"] = Of(factory.Configure(repository, guest, resource, revision, "setup", "manual", null, null)),
            ["rotation"] = Of(factory.RotateAccess(repository, guest, resource, revision, new AccessRotationContext(1, 2, [1]))),
            ["service"] = Of(factory.Pipeline(repository, repository.Service(ServiceDefinitionTests.Address)!,
                new Resource { Id = Guid.NewGuid(), Address = ServiceDefinitionTests.Address, Kind = ResourceKinds.Service, Lab = "home" },
                resource, revision, "manual")),
        };
    }

    [Fact]
    public void Each_run_takes_exactly_one_source_in_the_agreed_order()
    {
        var id = Guid.NewGuid();
        // 1. A declared key wins over both automatic sources.
        using (var test = ManagedVm(declared: true))
            Assert.All(Sources(test, id), s => Assert.Equal((Manual, false), s.Value));
        using (var test = Lab("container", hostKey: true))
            Assert.All(Sources(test, id), s => Assert.Equal((Manual, false), s.Value));
        // 2. A container names none: its create task's pin, never the managed key.
        using (var test = Lab("container", hostKey: false))
            Assert.All(Sources(test, id), s => Assert.Equal((ContainerHostKeys.Key(id), false), s.Value));
        // 3. A VM whose workflow opts in names none: the managed key, binding no store pin, never a container's.
        using (var test = ManagedVm(declared: false))
            Assert.All(Sources(test, id), s => Assert.Equal(((string?)null, true), s.Value));
    }

    [Fact]
    public void Trust_of_each_kind_is_decided_in_one_place()
    {
        using var vm = ManagedVm(declared: false);
        var repository = LabRepositoryLoader.Load(vm.Root);
        var guest = repository.Guest(Guest)!;
        Assert.Equal(HostKeyTrust.Managed, ManagedHostKey.TrustOf(repository, guest));
        Assert.False(ContainerHostKey.Pinned(guest));
        Assert.False(ContainerHostKey.Wanted(repository, guest));

        using var declared = ManagedVm(declared: true);
        repository = LabRepositoryLoader.Load(declared.Root);
        Assert.Equal(HostKeyTrust.Declared, ManagedHostKey.TrustOf(repository, repository.Guest(Guest)!));

        using var container = Lab("container", hostKey: false);
        repository = LabRepositoryLoader.Load(container.Root);
        guest = repository.Guest(Guest)!;
        Assert.Equal(HostKeyTrust.Container, ManagedHostKey.TrustOf(repository, guest));
        Assert.False(ManagedHostKey.Offered(repository, guest));
        Assert.True(ContainerHostKey.Pinned(guest));

        // A VM that names none and whose workflow does not opt in has no source: its runs are refused.
        using var plain = Lab("vm", hostKey: false);
        repository = LabRepositoryLoader.Load(plain.Root);
        Assert.Equal(HostKeyTrust.None, ManagedHostKey.TrustOf(repository, repository.Guest(Guest)!));
    }

    [Fact]
    public void A_container_workflow_with_a_managed_host_key_is_refused_by_the_loader_and_the_validator()
    {
        using var test = Lab("container", hostKey: false);
        var workflow = Path.Combine(test.Root, "workflows/app/workflow.yaml");
        var text = File.ReadAllText(workflow);
        Assert.Contains("    root: ../../modules/opentofu/test-vm\n", text);
        test.Write("workflows/app/workflow.yaml", text.Replace("    root: ../../modules/opentofu/test-vm\n", "    root: ../../modules/opentofu/test-vm\n    hostKey: managed\n"));

        var repository = LabRepositoryLoader.Load(test.Root);
        var issue = Assert.Single(repository.Issues, i => i.Severity == IssueSeverity.Error && i.Address == "workflows/app");
        Assert.Contains("cannot have hostKey: managed: a container's SSH host keys are made by Proxmox", issue.Message);
        Assert.False(repository.IsValid(Guest));

        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(Octopushy.Validate.Validator.Invalid, Octopushy.Validate.Validator.Run([test.Root], output, error));
        Assert.Contains("cannot have hostKey: managed", output.ToString() + error.ToString());
    }
}
