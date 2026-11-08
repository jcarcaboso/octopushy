using System.Text.Json.Nodes;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>What the run page shows of a plan's changes and a preview's baseline.</summary>
public class PlanViewTests
{
    private static Step PlanStep(params JsonObject[] attributes) => new()
    {
        Name = StepNames.Plan,
        Kind = StepKinds.Agent,
        Outputs = new JsonObject
        {
            ["hasChanges"] = true,
            ["changes"] = new JsonArray(new JsonObject
            {
                ["address"] = "proxmox_virtual_environment_vm.guest",
                ["type"] = "proxmox_virtual_environment_vm",
                ["name"] = "guest",
                ["actions"] = new JsonArray("update"),
                ["attributes"] = new JsonArray(attributes.Select(a => (JsonNode)a).ToArray()),
            }),
            ["counts"] = new JsonObject { ["update"] = 1 },
        }.ToJsonString(),
    };

    [Fact]
    public void A_change_carries_its_allowlisted_attributes()
    {
        var plan = Views.Plan(PlanStep(
            new JsonObject { ["name"] = "disk.scsi0.size", ["before"] = 32, ["after"] = 64 },
            new JsonObject { ["name"] = "memory.dedicated", ["before"] = 4096, ["after"] = 8192 },
            new JsonObject { ["name"] = "tags", ["before"] = new JsonArray("old"), ["after"] = new JsonArray("agents", "old") },
            new JsonObject { ["name"] = "network_device.0.bridge", ["before"] = "vmbr0", ["unknown"] = true },
            new JsonObject { ["name"] = "cpu.cores", ["before"] = 2, ["after"] = 4, ["sensitive"] = true }))!;

        var attributes = Assert.Single(plan.Changes).Attributes;
        Assert.Equal(new[] { "disk.scsi0.size", "memory.dedicated", "tags", "network_device.0.bridge", "cpu.cores" }, attributes.Select(a => a.Name));
        Assert.Equal((32, 64), (attributes[0].Before!.GetValue<int>(), attributes[0].After!.GetValue<int>()));
        Assert.Equal(["agents", "old"], attributes[2].After!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.True(attributes[3].Unknown);
        Assert.Null(attributes[3].After);
        Assert.True(attributes[4].Sensitive);
        Assert.Null(attributes[4].Before);
        Assert.Null(attributes[4].After);
    }

    [Fact]
    public void An_attribute_off_the_allowlist_or_with_an_object_value_is_dropped()
    {
        var plan = Views.Plan(PlanStep(
            new JsonObject { ["name"] = "cloud_init.user_data", ["before"] = "a", ["after"] = "b" },
            new JsonObject { ["name"] = "disk.scsi0.size\n", ["before"] = 32, ["after"] = 64 },
            new JsonObject { ["name"] = "memory.dedicated", ["before"] = 4096, ["after"] = new JsonObject { ["password"] = "x" } },
            new JsonObject { ["name"] = "cpu.cores", ["before"] = new JsonArray(1), ["after"] = 2 },
            new JsonObject { ["name"] = "tags", ["before"] = new JsonArray(new JsonObject()), ["after"] = new JsonArray("a") },
            new JsonObject { ["name"] = "cpu.sockets", ["before"] = 1, ["after"] = 2 }))!;

        Assert.Equal("cpu.sockets", Assert.Single(Assert.Single(plan.Changes).Attributes).Name);
    }

    [Fact]
    public void A_change_without_attributes_has_none()
    {
        var step = new Step
        {
            Name = StepNames.Plan,
            Kind = StepKinds.Agent,
            Outputs = """{"changes":[{"address":"terraform_data.r0","actions":["create"]}]}""",
        };
        Assert.Empty(Assert.Single(Views.Plan(step)!.Changes).Attributes);
    }

    [Fact]
    public void A_run_made_before_the_baseline_was_kept_shows_none()
    {
        var context = RunContext.Parse("""{"address":"a","proxmox":{"endpoint":"e","verifyTls":true,"node":"n","vmid":1}}""");
        Assert.Null(context.PreviousPrepared);
        var step = new Step { Name = StepNames.Preview, Kind = StepKinds.Agent, Outputs = """{"action":"setup","playbook":"site.yml","prepared":{"version":"1"}}""" };
        Assert.Null(Views.ConfigurePreview(step, context.PreviousPrepared)!.PreviousPrepared);
    }
}
