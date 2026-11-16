using System.Text.Json.Nodes;
using Octopushy.Server.Data;
using Octopushy.Server.Inventory;

namespace Octopushy.Tests.Server;

/// <summary>A guest's own machine gone, or moved to another host of its cluster, as the installation's latest observation shows it.</summary>
public class MachineDriftTests
{
    private static readonly DateTimeOffset BoundAt = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    private static Resource Guest(int? vmid = 310, string? installation = "home/korriban") => new()
    {
        Address = "home/korriban/krb-app-01",
        Kind = ResourceKinds.Guest,
        Lab = "home",
        ParentAddress = "home/korriban",
        BoundVmid = vmid,
        BoundInstallation = vmid is null ? null : installation,
        BoundAt = vmid is null ? null : BoundAt,
    };

    private static JsonObject Observation(JsonArray guests, JsonNode? cluster = null, bool complete = true, DateTimeOffset? at = null) => new()
    {
        ["observedAt"] = at ?? BoundAt.AddHours(1),
        ["cluster"] = cluster,
        ["vmVisibility"] = new JsonObject { ["complete"] = complete },
        ["hosts"] = new JsonArray(new JsonObject { ["name"] = "korriban", ["online"] = true }),
        ["guests"] = guests,
    };

    private static JsonObject Vm(int vmid, string host) => new() { ["vmid"] = vmid, ["host"] = host, ["kind"] = "qemu" };

    private static JsonObject Quorate(bool quorate) => new() { ["name"] = "home", ["quorate"] = quorate };

    [Fact]
    public void A_bound_vm_that_no_host_lists_is_missing()
    {
        var drift = MachineDrift.Of(Guest(), "korriban", Observation([Vm(311, "korriban")]));

        Assert.NotNull(drift);
        Assert.Equal((MachineDriftKinds.Missing, 310, (string?)null), (drift.Kind, drift.Vmid, drift.ObservedHost));
        Assert.Contains("restore it from a backup at VMID 310", drift.Detail);
    }

    [Fact]
    public void A_bound_vm_on_another_host_of_its_cluster_has_moved()
    {
        var drift = MachineDrift.Of(Guest(), "korriban", Observation([Vm(310, "dromund")], Quorate(true)));

        Assert.NotNull(drift);
        Assert.Equal((MachineDriftKinds.Moved, "korriban", "dromund"), (drift.Kind, drift.DeclaredHost, drift.ObservedHost));
        Assert.Contains("host: dromund", drift.Detail);
    }

    [Fact]
    public void A_bound_vm_where_its_definition_places_it_has_no_drift() =>
        Assert.Null(MachineDrift.Of(Guest(), "korriban", Observation([Vm(310, "korriban")], Quorate(true))));

    [Fact]
    public void An_observation_that_cannot_prove_it_saw_every_vm_says_nothing()
    {
        Assert.Null(MachineDrift.Of(Guest(), "korriban", Observation([], complete: false)));
        Assert.Null(MachineDrift.Of(Guest(), "korriban", Observation([], Quorate(false))));
        var unsaid = Observation([]);
        unsaid.Remove("cluster");
        Assert.Null(MachineDrift.Of(Guest(), "korriban", unsaid));
    }

    [Fact]
    public void An_observation_from_before_the_guest_was_bound_says_nothing() =>
        Assert.Null(MachineDrift.Of(Guest(), "korriban", Observation([], at: BoundAt.AddMinutes(-1))));

    [Fact]
    public void A_guest_bound_to_nothing_or_elsewhere_has_no_drift()
    {
        Assert.Null(MachineDrift.Of(Guest(vmid: null), "korriban", Observation([])));
        Assert.Null(MachineDrift.Of(Guest(installation: "home/moraband"), "korriban", Observation([])));
        Assert.Null(MachineDrift.Of(Guest(), "korriban", null));
        var retired = Guest();
        retired.RetiredAt = BoundAt.AddDays(1);
        Assert.Null(MachineDrift.Of(retired, "korriban", Observation([])));
    }
}
