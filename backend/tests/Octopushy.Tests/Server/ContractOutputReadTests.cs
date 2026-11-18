using System.Text.Json.Nodes;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>
/// What the backend reads of the outputs in contracts/agent/v1/examples, which the agent's tests produce from its own code.
/// Each reader is given an example and has to come out with the example's values: a field it reads that the example lacks,
/// or reads under another name, comes out empty and fails. <see cref="ContractExampleTests"/> covers the readers inside the
/// run engine.
/// </summary>
public class ContractOutputReadTests
{
    /// <summary>The example lab's guest, which the input examples are built for.</summary>
    private static readonly RunContext Guest = new()
    {
        Address = "home/korriban/krb-app-01",
        Proxmox = new ProxmoxContext("https://10.1.0.10:8006/", false, null, "korriban", 310, "krb-app-01", "10.1.0.31", "qemu"),
    };

    private static JsonObject Example(string executor, string action, string? variant = null) =>
        ContractExamples.Output(executor, action, variant);

    [Fact]
    public void The_plan_view_shows_every_field_of_the_preview_example()
    {
        var example = Example("opentofu", "preview");
        var plan = Views.Plan(new Step { Kind = StepKinds.Agent, Name = StepNames.Plan, Outputs = example.ToJsonString() })!;

        Assert.Equal(example["hasChanges"]!.GetValue<bool>(), plan.HasChanges);
        Assert.Equal(example["toolVersion"]!.GetValue<string>(), plan.ToolVersion);
        var counts = example["counts"]!;
        Assert.Equal((counts["create"]!.GetValue<int>(), counts["update"]!.GetValue<int>(), counts["delete"]!.GetValue<int>(), counts["replace"]!.GetValue<int>()),
            (plan.Counts.Create, plan.Counts.Update, plan.Counts.Delete, plan.Counts.Replace));
        var changes = example["changes"]!.AsArray();
        Assert.Equal(changes.Count, plan.Changes.Count);
        foreach (var (change, shown) in changes.Zip(plan.Changes))
        {
            Assert.Equal(change!["address"]!.GetValue<string>(), shown.Address);
            Assert.Equal(change["type"]!.GetValue<string>(), shown.Type);
            Assert.Equal(change["name"]!.GetValue<string>(), shown.Name);
            Assert.Equal(change["actions"]!.AsArray().Select(a => a!.GetValue<string>()), shown.Actions);
            var attributes = change["attributes"]?.AsArray() ?? [];
            Assert.Equal(attributes.Select(a => a!["name"]!.GetValue<string>()), shown.Attributes.Select(a => a.Name));
            Assert.All(attributes.Zip(shown.Attributes), pair => Assert.True(JsonNode.DeepEquals(pair.First!["after"], pair.Second.After)));
        }
        Assert.NotEmpty(plan.Changes.SelectMany(c => c.Attributes));
    }

    [Fact]
    public void The_safety_boundary_reads_the_guests_machine_and_every_target_from_the_preview_example()
    {
        var example = Example("opentofu", "preview");
        // type, actions, vmId and node: the plan creates the guest's own VM.
        Assert.True(GuestVmPlan.MakesGuestMachine(example, Guest));
        // proxmox and targets: every Proxmox change names only the guest's VM or node. Naming another VM is refused.
        Assert.Null(GuestVmPlan.ForeignChange(example, Guest));
        var elsewhere = example.DeepClone().AsObject();
        foreach (var change in elsewhere["changes"]!.AsArray())
            change!["targets"] = new JsonArray(new JsonObject { ["vmId"] = 999, ["node"] = "korriban" });
        Assert.NotNull(GuestVmPlan.ForeignChange(elsewhere, Guest));
        // importing: an import is refused, and the example has none.
        Assert.All(example["changes"]!.AsArray(), c => Assert.False(c!["importing"]!.GetValue<bool>()));
    }

    [Fact]
    public void A_verified_machine_is_the_guests_by_the_tags_description_and_node_of_the_verify_example()
    {
        var example = Example("proxmox", "verify-guest");
        Assert.Null(SafetyBoundary.MarksMissing(example, Guest));
        Assert.NotNull(SafetyBoundary.MarksMissing(example, Guest with { Address = "home/korriban/krb-app-02" }));
    }

    [Fact]
    public void The_configure_preview_view_shows_every_field_of_the_ansible_preview_example()
    {
        var example = Example("ansible", "preview");
        var preview = Views.ConfigurePreview(new Step { Kind = StepKinds.Agent, Name = StepNames.Preview, Outputs = example.ToJsonString() })!;
        var manifest = example["manifest"]!;

        Assert.Equal(example["action"]!.GetValue<string>(), preview.Action);
        Assert.Equal(example["playbook"]!.GetValue<string>(), preview.Playbook);
        Assert.Equal(example["toolVersion"]!.GetValue<string>(), preview.ToolVersion);
        Assert.Equal(example["tasks"]!.AsArray().Select(t => t!.GetValue<string>()), preview.Tasks);
        Assert.Equal(example["hostKeyFingerprints"]!.AsArray().Select(t => t!.GetValue<string>()), preview.HostKeyFingerprints);
        Assert.True(JsonNode.DeepEquals(example["prepared"], preview.Prepared));
        Assert.Equal(manifest["commit"]!.GetValue<string>(), preview.Commit);
        Assert.Equal(manifest["image"]!.GetValue<string>(), preview.Image);
        Assert.Equal(manifest["host"]!.GetValue<string>(), preview.Host);
        Assert.Equal(manifest["destructive"]!.GetValue<bool>(), preview.Destructive);
        Assert.True(JsonNode.DeepEquals(manifest["vars"], preview.Vars));
        Assert.Equal(manifest["secrets"]!.AsObject().Select(p => (p.Key, p.Value!.GetValue<string>())),
            preview.Secrets.Select(s => (s.Name, s.Reference)));
    }
}
