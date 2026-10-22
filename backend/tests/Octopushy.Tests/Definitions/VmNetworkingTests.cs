using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Definitions;

public class VmNetworkingTests
{
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";

    [Theory]
    [InlineData("mode: dhcp", null)]
    [InlineData("mode: dhcp, mac: '02:00:00:00:00:31'", null)]
    [InlineData("mode: dhcp, mac: '02:00:00:00:00:31', reservedAddress: 10.1.0.31", "10.1.0.31")]
    [InlineData("mode: dhcp, mac: 'AA:BB:CC:DD:EE:02', reservedAddress: 192.168.1.2", "192.168.1.2")]
    [InlineData("mode: dhcp, mac: '00:50:56:00:00:31', reservedAddress: 172.16.0.31", "172.16.0.31")]
    [InlineData("mode: static, address: 10.1.0.31/24, gateway: 10.1.0.1", "10.1.0.31")]
    [InlineData("mode: static, address: 10.1.0.31/32, gateway: 10.1.0.1, mac: '02:00:00:00:00:31'", "10.1.0.31")]
    [InlineData("mode: static, address: 10.1.0.31/0, gateway: 10.1.0.1", "10.1.0.31")]
    public void Valid_networks_preserve_provisioning_and_verification_intent(string network, string? expectedIpv4)
    {
        using var test = WithNetwork(network);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        var guest = Assert.Single(repository.Guests);
        Assert.True(repository.IsValid(guest.Address));

        var inputs = ProvisionInputs.Build(repository, guest, "rev");
        var provisionNetwork = inputs.Variables["guest"]!["network"]!;
        Assert.True(JsonNode.DeepEquals(guest.Json["network"], provisionNetwork));
        Assert.Equal(expectedIpv4, ProvisionInputs.ExpectedIpv4(guest));
        if (provisionNetwork["mode"]!.GetValue<string>() == "dhcp")
        {
            Assert.Null(provisionNetwork["address"]);
            Assert.Null(provisionNetwork["gateway"]);
        }

        // Exercise the real run construction: this context supplies the agent's expectIpv4.
        var run = new RunFactory(Options.Create(new OctopushyOptions()), TimeProvider.System).Guest(
            repository,
            guest,
            new Resource { Kind = ResourceKinds.Guest, Address = guest.Address, Lab = guest.Lab },
            new DefinitionRevision { Commit = "rev" },
            "test");
        var context = RunContext.Parse(run.Context);
        Assert.Equal("proxmox", context.Verify);
        Assert.Equal(expectedIpv4, context.Proxmox.ExpectIpv4);
        Assert.Contains(run.Steps, step => step.Name == StepNames.Verify && step.Status != StepStatuses.Skipped);
    }

    [Theory]
    [InlineData("mode: dhcp, reservedAddress: 10.1.0.31")]
    [InlineData("mode: dhcp, address: 10.1.0.31/24")]
    [InlineData("mode: dhcp, gateway: 10.1.0.1")]
    [InlineData("mode: dhcp, address: null, gateway: null")]
    [InlineData("mode: dhcp, mac: '02:00:00:00:00:31', reservedAddress: 10.1.0.31, gateway: 10.1.0.1")]
    [InlineData("mode: static, address: 10.1.0.31/24, gateway: 10.1.0.1, mac: '02:00:00:00:00:31', reservedAddress: 10.1.0.31")]
    [InlineData("mode: static, address: 10.1.0.31/24")]
    [InlineData("mode: static, gateway: 10.1.0.1")]
    [InlineData("mode: static, address: 10.1.0.31, gateway: 10.1.0.1")]
    [InlineData("mode: static, address: 256.1.0.31/24, gateway: 10.1.0.1")]
    [InlineData("mode: static, address: 10.01.0.31/24, gateway: 10.1.0.1")]
    [InlineData("mode: static, address: 10.1.0.31/33, gateway: 10.1.0.1")]
    [InlineData("mode: static, address: 10.1.0.31/-1, gateway: 10.1.0.1")]
    [InlineData("mode: static, address: 10.1.0.31/024, gateway: 10.1.0.1")]
    [InlineData("mode: static, address: '2001:db8::1/64', gateway: 10.1.0.1")]
    [InlineData("mode: static, address: 10.1.0.31/24, gateway: 10.1.0.256")]
    [InlineData("mode: static, address: 10.1.0.31/24, gateway: 10.1.0.1/24")]
    [InlineData("mode: static, address: \"10.1.0.31/24\\n\", gateway: 10.1.0.1")]
    [InlineData("mode: static, address: 10.1.0.31/24, gateway: \"10.1.0.1\\n\"")]
    [InlineData("mode: dhcp, mac: '02:00:00:00:00:31', reservedAddress: 10.1.0.256")]
    [InlineData("mode: dhcp, mac: '02:00:00:00:00:31', reservedAddress: 10.01.0.31")]
    [InlineData("mode: dhcp, mac: '02:00:00:00:00:31', reservedAddress: 10.1.0.31/24")]
    [InlineData("mode: dhcp, mac: '02:00:00:00:00:31', reservedAddress: not-an-ip")]
    [InlineData("mode: dhcp, mac: '02:00:00:00:00:31', reservedAddress: \"10.1.0.31\\n\"")]
    [InlineData("mode: dhcp, mac: '02:00:00:00:00:31', reservedAddress: null")]
    [InlineData("mode: dhcp, mac: null, reservedAddress: 10.1.0.31")]
    [InlineData("mode: dhcp, mac: '02-00-00-00-00-31', reservedAddress: 10.1.0.31")]
    [InlineData("mode: dhcp, mac: '02:00:00:00:00:GG', reservedAddress: 10.1.0.31")]
    [InlineData("mode: dhcp, mac: '01:00:00:00:00:31', reservedAddress: 10.1.0.31")]
    [InlineData("mode: dhcp, mac: 'ff:ff:ff:ff:ff:ff', reservedAddress: 10.1.0.31")]
    [InlineData("mode: dhcp, mac: '00:00:00:00:00:00', reservedAddress: 10.1.0.31")]
    [InlineData("mode: dhcp, mac: 'not-a-mac'")]
    [InlineData("mode: dhcp, mac: \"02:00:00:00:00:31\\n\", reservedAddress: 10.1.0.31")]
    [InlineData("mode: static, address: 10.1.0.31/24, gateway: 10.1.0.1, mac: '01:00:00:00:00:31'")]
    [InlineData("mode: reserved, mac: '02:00:00:00:00:31', reservedAddress: 10.1.0.31")]
    public void Invalid_networks_are_reported_on_the_guest_by_the_recipe(string network)
    {
        using var test = WithNetwork(network);
        var repository = LabRepositoryLoader.Load(test.Root);
        var guest = Assert.Single(repository.Guests);
        Assert.False(repository.IsValid(guest.Address));
        Assert.Contains(repository.Issues, issue =>
            issue.Severity == IssueSeverity.Error &&
            issue.Address == guest.Address &&
            issue.File == GuestFile &&
            issue.Line is not null &&
            issue.Message.Contains("recipe test-vm"));
        Assert.True(repository.IsValid("home/korriban"));
    }

    [Fact]
    public void Changing_the_reservation_changes_the_inputs_hash()
    {
        using var test = WithNetwork("mode: dhcp, mac: '02:00:00:00:00:31', reservedAddress: 10.1.0.31");
        var before = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(before.Issues);
        var hash = ProvisionInputs.Build(before, before.Guests[0], "rev").Hash;
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile)).Replace("10.1.0.31", "10.1.0.32"));
        var after = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(after.Issues);
        Assert.NotEqual(hash, ProvisionInputs.Build(after, after.Guests[0], "rev").Hash);
        Assert.Equal("10.1.0.32", ProvisionInputs.ExpectedIpv4(after.Guests[0]));
    }

    private static TestRepository WithNetwork(string network)
    {
        var test = TestRepository.Minimal();
        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile))
            .Replace("hardware: { cores: 2, memory: 2048 }", "hardware: { cores: 2, memory: 2048, disks: [{ size: 16 }] }")
            .Replace("network: { bridge: vmbr0, mode: static, address: 10.1.0.31/24, gateway: 10.1.0.1 }",
                $"network: {{ bridge: vmbr0, {network} }}"));
        test.Write("recipes/test-vm/recipe.yaml",
            File.ReadAllText(Path.Combine(test.Root, "recipes/test-vm/recipe.yaml")) + "\ninputs: inputs.schema.json");
        test.Write("recipes/test-vm/inputs.schema.json",
            File.ReadAllText(Path.Combine(TestRepository.ExampleLab, "recipes/proxmox-vm/inputs.schema.json")));
        return test;
    }
}
