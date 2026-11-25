using System.Text.Json.Nodes;
using Octopushy.Server.Api;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>What a list of runs says a run changes, as <see cref="RunChanges"/> stores it on the run.</summary>
public class RunChangesTests
{
    private static RunContext Context(JsonNode? previous = null, ComposeImagesContext? images = null) => new()
    {
        Address = "home/korriban/krb-app-01",
        Proxmox = new ProxmoxContext("https://10.1.0.10:8006/", false, null, "korriban", 310, "krb-app-01", null),
        PreviousPrepared = previous,
        ComposeImages = images,
    };

    private static Step Preview(JsonNode? prepared, int ordinal = 0) => new()
    {
        Name = StepNames.Preview,
        Kind = StepKinds.Agent,
        Ordinal = ordinal,
        Outputs = new JsonObject { ["action"] = "setup", ["prepared"] = prepared?.DeepClone() }.ToJsonString(),
    };

    private static JsonObject Change(string address, string[] actions, params JsonObject[] attributes) => new()
    {
        ["address"] = address,
        ["actions"] = new JsonArray(actions.Select(a => (JsonNode)a).ToArray()),
        ["attributes"] = new JsonArray(attributes.Select(a => (JsonNode)a).ToArray()),
    };

    private static Step Plan(int ordinal, params JsonObject[] changes) => new()
    {
        Name = StepNames.Plan,
        Kind = StepKinds.Agent,
        Ordinal = ordinal,
        Outputs = new JsonObject { ["hasChanges"] = changes.Length > 0, ["changes"] = new JsonArray(changes.Select(c => (JsonNode)c).ToArray()) }.ToJsonString(),
    };

    private static JsonObject Attribute(string name, JsonNode? before, JsonNode? after, bool unknown = false, bool sensitive = false) => new()
    {
        ["name"] = name,
        ["before"] = before,
        ["after"] = after,
        ["unknown"] = unknown,
        ["sensitive"] = sensitive,
    };

    private static string Lines(IEnumerable<RunChangeDto>? changes) =>
        changes is null ? "null" : string.Join(" | ", changes.Select(c => $"{c.Name}: {c.Before ?? "null"} -> {c.After ?? "null"}"));

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var item in Examples())
            data.Add(item!["name"]!.GetValue<string>());
        return data;
    }

    private static JsonArray Examples() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(TestRepository.SourceRoot, "contracts", "examples", "run-changes.json")))!.AsArray();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Prepared_values_compare_as_the_frontend_compares_them(string name)
    {
        var example = Examples().Single(e => e!["name"]!.GetValue<string>() == name)!;
        var expected = example["changes"] is JsonArray lines
            ? lines.Select(l => new RunChangeDto(l!["name"]!.GetValue<string>(), l["before"]?.GetValue<string>(), l["after"]?.GetValue<string>())).ToList()
            : null;

        Assert.Equal(Lines(expected), Lines(RunChanges.PreparedChanges(example["prepared"], example["previous"])));
        // Through a preview step as an agent reports it, and the run's baseline as its context keeps it.
        Assert.Equal(Lines(expected), Lines(RunChanges.Of(Context(example["previous"]?.DeepClone()), [Preview(example["prepared"])])));
    }

    [Fact]
    public void Every_example_is_checked()
    {
        Assert.True(Examples().Count >= 18);
        Assert.Equal(Examples().Count, Cases().Count);
    }

    [Fact]
    public void Numbers_read_as_javascript_prints_them()
    {
        var changes = RunChanges.PreparedChanges(JsonNode.Parse("""{"a":8.0,"b":1e21,"c":0.0000001,"d":1.5,"e":[1,2.50],"f":100}"""),
            JsonNode.Parse("""{"a":8,"b":1e20,"c":0.000001,"d":1.25,"e":[1,2.5],"f":1e2}"""));
        Assert.Equal("b: 100000000000000000000 -> 1e+21 | c: 0.000001 -> 1e-7 | d: 1.25 -> 1.5", Lines(changes));
    }

    [Fact]
    public void Keys_that_are_array_indexes_come_first_as_javascript_orders_them()
    {
        var changes = RunChanges.PreparedChanges(JsonNode.Parse("""{"b":2,"10":2,"2":2,"02":2}"""), new JsonObject());
        Assert.Equal("2: null -> 2 | 10: null -> 2 | b: null -> 2 | 02: null -> 2 | (value): {} -> null", Lines(changes));
    }

    [Fact]
    public void Images_name_the_services_whose_image_changed_by_version()
    {
        var images = new ComposeImagesContext("abc",
        [
            new ComposeImageChange("web", "nginx:1.26", "nginx:1.27"),
            new ComposeImageChange("same", "redis:7", "redis:7"),
            new ComposeImageChange("db", "postgres:17@sha256:0123456789abcdef0123", "postgres:17@sha256:fedcba9876543210fedc"),
            new ComposeImageChange("pinned", "registry:5000/team/app@sha256:aaaaaaaaaaaaaaaa", "registry:5000/team/app:2.0@sha256:bbbbbbbbbbbbbbbb"),
            new ComposeImageChange("new", null, "ghcr.io/x/y"),
            new ComposeImageChange("gone", "busybox:1", null),
        ]);

        Assert.Equal(
            "web: 1.26 -> 1.27 | db: 17@sha256:0123456789ab -> 17@sha256:fedcba987654 | pinned: sha256:aaaaaaaaaaaa -> 2.0 | new: null -> no tag | gone: 1 -> null",
            Lines(RunChanges.Of(Context(images: images), [])));
        Assert.Equal("", Lines(RunChanges.Of(Context(images: new ComposeImagesContext(null, [new ComposeImageChange("web", "nginx:1", "nginx:1")])), [])));
        Assert.Null(RunChanges.Of(Context(), []));
    }

    [Fact]
    public void A_plan_lists_the_settings_it_changes_then_the_resources_that_change_otherwise()
    {
        var plan = Plan(0,
            Change("proxmox_virtual_environment_vm.guest", ["update"],
                Attribute("disk.scsi0.size", 32, 64),
                Attribute("memory.dedicated", 4096, 8192),
                Attribute("cpu.cores", 2, 4),
                Attribute("cpu.sockets", 1, 1),
                Attribute("network_device.0.bridge", "vmbr0", "vmbr1"),
                Attribute("tags", new JsonArray("old"), new JsonArray("agents", "old")),
                Attribute("disk.scsi1.size", 8, null, unknown: true),
                Attribute("description", "a", "b")),
            Change("proxmox_virtual_environment_vm.other", ["update"], Attribute("memory.dedicated", 1000, 2048)),
            Change("proxmox_virtual_environment_vm.secret", ["update"], Attribute("cpu.cores", null, null, sensitive: true)),
            Change("terraform_data.a", ["create"]),
            Change("terraform_data.b", ["create"]),
            Change("terraform_data.c", ["delete", "create"]),
            Change("terraform_data.d", ["create", "delete"]),
            Change("terraform_data.e", ["delete"]),
            Change("terraform_data.f", ["no-op"]),
            Change("data.x", ["read"]),
            Change("terraform_data.g", ["create", "update"]));

        Assert.Equal(
            "disk scsi0: 32 GiB -> 64 GiB | memory: 4 GiB -> 8 GiB | cpu cores: 2 -> 4 | net0 bridge: vmbr0 -> vmbr1 | tags: old -> agents, old"
            + " | memory: 1000 MiB -> 2048 MiB | resources: null -> 2 to create, 1 to update, 2 to replace, 1 to delete, 1 other",
            Lines(RunChanges.Of(Context(), [plan])));
    }

    [Fact]
    public void Resources_are_counted_in_a_fixed_order()
    {
        var plan = Plan(0, Change("a.a", ["delete"]), Change("a.b", ["update"]), Change("a.c", ["create"]));
        Assert.Equal("resources: null -> 1 to create, 1 to update, 1 to delete", Lines(RunChanges.Of(Context(), [plan])));
        Assert.Equal("", Lines(RunChanges.Of(Context(), [Plan(0, Change("a.a", ["no-op"]))])));
    }

    [Fact]
    public void Tags_with_none_on_one_side_read_null_there()
    {
        var plan = Plan(0, Change("vm.a", ["create"], Attribute("tags", null, new JsonArray("a", "b"))),
            Change("vm.b", ["update"], Attribute("tags", new JsonArray("a"), new JsonArray())));
        Assert.Equal("tags: null -> a, b | tags: a -> null", Lines(RunChanges.Of(Context(), [plan])));
    }

    [Fact]
    public void Stages_follow_step_order_and_images_come_last()
    {
        var images = new ComposeImagesContext("abc", [new ComposeImageChange("web", "nginx:1.26", "nginx:1.27")]);
        var steps = new[]
        {
            Preview(new JsonObject { ["version"] = "2" }, ordinal: 1),
            Plan(0, Change("a.a", ["create"])),
            new Step { Name = StepNames.Approval, Kind = StepKinds.Approval, Ordinal = 2 },
        };
        Assert.Equal("resources: null -> 1 to create | version: 1 -> 2 | web: 1.26 -> 1.27",
            Lines(RunChanges.Of(Context(new JsonObject { ["version"] = "1" }, images), steps)));
    }

    [Fact]
    public void Nothing_compared_is_null_and_nothing_new_is_empty()
    {
        Assert.Null(RunChanges.Of(Context(), [Preview(new JsonObject { ["a"] = 1 })]));
        Assert.Null(RunChanges.Of(Context(new JsonObject { ["a"] = 1 }), [Preview(null)]));
        Assert.Null(RunChanges.Of(Context(new JsonObject { ["a"] = 1 }), [new Step { Name = StepNames.Preview, Kind = StepKinds.Agent }]));
        Assert.Null(RunChanges.Of(null, [new Step { Name = StepNames.Plan, Kind = StepKinds.Agent }]));
        Assert.Equal([], RunChanges.Of(Context(new JsonObject { ["a"] = 1 }), [Preview(new JsonObject { ["a"] = 1 })]));
        Assert.Equal([], RunChanges.Of(null, [Plan(0)]));
    }

    [Fact]
    public void The_list_and_each_text_are_capped()
    {
        var prepared = new JsonObject();
        for (var i = 0; i < 30; i++)
            prepared[$"k{i}"] = new string('x', 500);
        var changes = RunChanges.Of(Context(new JsonObject()), [Preview(prepared)])!;
        Assert.Equal(RunChanges.MaxLines, changes.Count);
        Assert.All(changes, c => Assert.Equal(RunChanges.MaxText, c.After!.Length));
        Assert.EndsWith("…", changes[0].After);

        var name = new string('n', 300);
        var images = new ComposeImagesContext(null, [new ComposeImageChange(name, null, "nginx:" + new string('1', 300))]);
        var line = Assert.Single(RunChanges.Of(Context(images: images), [])!);
        Assert.Equal(RunChanges.MaxText, line.Name.Length);
        Assert.Equal(RunChanges.MaxText, line.After!.Length);

        // A surrogate pair is not cut in half.
        var emoji = RunChanges.Cap(new string('a', RunChanges.MaxText - 2) + "😀😀");
        Assert.Equal(new string('a', RunChanges.MaxText - 2) + "…", emoji);
    }

    [Fact]
    public void Malformed_outputs_and_columns_never_throw()
    {
        var steps = new[]
        {
            new Step { Name = StepNames.Preview, Kind = StepKinds.Agent, Ordinal = 0, Outputs = "{not json" },
            new Step { Name = StepNames.Plan, Kind = StepKinds.Agent, Ordinal = 1, Outputs = """{"changes":[{"address":42,"actions":"x"}]}""" },
            new Step { Name = StepNames.Plan, Kind = StepKinds.Agent, Ordinal = 2, Outputs = "[1,2]" },
            Preview(JsonNode.Parse("""{"big":1e400,"deep":[[[["x"]]]]}"""), ordinal: 3),
            Plan(4, Change("vm.a", ["update"], Attribute("memory.dedicated", JsonNode.Parse("1e400"), "lots"), Attribute("tags", "notalist", new JsonArray("a")))),
        };
        var changes = RunChanges.Of(Context(new JsonObject { ["big"] = 1 }), steps);
        Assert.Equal("big: 1 -> null | deep[0][0][0]: null -> x | memory: null -> lots MiB | tags: null -> a", Lines(changes));

        var run = new Run { Operation = Operations.Configure, Reason = "manual", Context = "{broken", Steps = [Plan(0, Change("a.a", ["create"]))] };
        RunChanges.Refresh(run);
        Assert.Equal("resources: null -> 1 to create", Lines(RunChanges.Read(run.Changes)));

        Assert.Null(RunChanges.Read(null));
        Assert.Null(RunChanges.Read("{broken"));
        Assert.Null(RunChanges.Read("{\"name\":\"a\"}"));
        Assert.Equal("a: null -> x", Lines(RunChanges.Read("""[{"name":"a","before":5,"after":"x"},{"before":"no name"},7,null]""")));
        Assert.Equal(RunChanges.MaxLines, RunChanges.Read("[" + string.Join(",", Enumerable.Repeat("""{"name":"a"}""", 50)) + "]")!.Count);
    }
}
