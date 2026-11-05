using System.Text.Json.Nodes;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>A container is a machine to every rule of a provision plan, as a VM is.</summary>
public class ContainerPlanTests
{
    private static RunContext Context(string kind) => new()
    {
        Address = "home/korriban/krb-app-01",
        Proxmox = new ProxmoxContext("https://10.1.0.10:8006/", false, null, "korriban", 310, "krb-app-01", null, kind),
    };

    private static JsonObject Plan(string type, int vmid, params string[] actions) => new()
    {
        ["changes"] = new JsonArray(new JsonObject
        {
            ["address"] = $"{type}.guest",
            ["type"] = type,
            ["actions"] = new JsonArray(actions.Select(a => (JsonNode)a).ToArray()),
            ["actionReason"] = "replace_because_tainted",
            ["vmId"] = vmid,
            ["node"] = "korriban",
            ["proxmox"] = true,
            ["targets"] = new JsonArray(new JsonObject { ["vmId"] = vmid, ["node"] = "korriban" }),
        }),
    };

    [Theory]
    [InlineData(GuestVmPlan.VmResourceType, "qemu")]
    [InlineData(GuestVmPlan.ContainerResourceType, "lxc")]
    public void Replacing_or_deleting_the_guests_machine_counts_alike(string type, string kind)
    {
        var context = Context(kind);
        Assert.True(GuestVmPlan.RemovesVm(Plan(type, 310, "delete", "create")));
        Assert.True(GuestVmPlan.RemovesGuestVm(Plan(type, 310, "delete"), context));
        Assert.True(GuestVmPlan.RecreatesFailedVm(Plan(type, 310, "delete", "create"), context));
        Assert.True(GuestVmPlan.MakesGuestMachine(Plan(type, 310, "create"), context));
        Assert.False(GuestVmPlan.RemovesGuestVm(Plan(type, 100, "delete"), context));
        Assert.Null(GuestVmPlan.ForeignChange(Plan(type, 310, "delete", "create"), context));
    }

    [Fact]
    public void A_change_to_another_machine_is_refused_and_named_by_the_guests_kind()
    {
        var refused = GuestVmPlan.ForeignChange(Plan(GuestVmPlan.ContainerResourceType, 100, "delete"), Context("lxc"));
        Assert.Contains("not this guest's container 310 on korriban", refused);
        refused = GuestVmPlan.ForeignChange(Plan(GuestVmPlan.ContainerResourceType, 100, "update"), Context("qemu"));
        Assert.Contains("not this guest's VM 310 on korriban", refused);
    }

    [Fact]
    public void A_run_made_before_containers_is_a_vms()
    {
        var context = new ProxmoxContext("https://10.1.0.10:8006/", false, null, "korriban", 310, "krb-app-01", null);
        Assert.Equal(("qemu", "VM"), (context.MachineKind, context.Machine));
        var parsed = RunContext.Parse("""{"address":"a","proxmox":{"endpoint":"e","verifyTls":true,"node":"n","vmid":1}}""");
        Assert.Null(parsed.Proxmox.Kind);
        Assert.Equal("qemu", parsed.Proxmox.MachineKind);
    }
}
