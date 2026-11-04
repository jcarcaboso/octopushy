using System.Text.Json.Nodes;
using Octopushy.Definitions.Yaml;

namespace Octopushy.Tests.Definitions;

/// <summary>A form's edit moves only the values that changed: comments, order and style stay as they were written.</summary>
public class YamlEditorTests
{
    private const string Guest = """
        schemaVersion: 1
        # The AI workstation.
        type: vm
        host: korriban            # moves with a migration
        vmid: 310
        workflow: fedora-vm@1
        hardware:
          cores: 2
          memory: 4096            # MiB
          disks:
            - { size: 32 }        # system disk
        network: { bridge: vmbr0, mode: static, address: 10.1.0.31/24, gateway: 10.1.0.1 }

        # Extra vars for setup.
        inputs:
          machine_features: [ai-tools, cli-tools]
          machine_git_user_name: 'Ops Person'
        policy: { approval: manual }

        """;

    private static JsonObject Read(string text) => YamlDocumentReader.Read(text).Root!.AsObject();

    private static string Edit(string text, Action<JsonObject> change)
    {
        var desired = Read(text);
        change(desired);
        var result = YamlEditor.Apply(text, desired);
        Assert.True(YamlEditor.Same(desired, Read(result)), result);
        return result;
    }

    [Fact]
    public void A_changed_value_moves_alone_and_keeps_its_comment()
    {
        var result = Edit(Guest, d => d["hardware"]!["memory"] = 8192);
        Assert.Equal(Guest.Replace("memory: 4096            # MiB", "memory: 8192            # MiB"), result);
    }

    [Fact]
    public void A_value_in_a_flow_mapping_is_replaced_in_place()
    {
        var result = Edit(Guest, d => d["network"]!["address"] = "10.1.0.32/24");
        Assert.Equal(Guest.Replace("address: 10.1.0.31/24,", "address: 10.1.0.32/24,"), result);
    }

    [Fact]
    public void A_flow_mapping_that_gains_a_key_is_written_again_in_flow_style_and_order()
    {
        var result = Edit(Guest, d =>
        {
            var network = d["network"]!.AsObject();
            network.Remove("bridge");
            network["vlan"] = 20;
            network["bridge"] = "vmbr1";
        });
        Assert.Equal(Guest.Replace("network: { bridge: vmbr0, mode: static, address: 10.1.0.31/24, gateway: 10.1.0.1 }",
            "network: { bridge: vmbr1, mode: static, address: 10.1.0.31/24, gateway: 10.1.0.1, vlan: 20 }"), result);
    }

    [Fact]
    public void A_new_key_goes_after_the_mappings_last_entry_and_a_removed_one_takes_its_lines()
    {
        var result = Edit(Guest, d =>
        {
            var hardware = d["hardware"]!.AsObject();
            hardware["cpuType"] = "host";
            hardware.Remove("cores");
        });
        Assert.Equal(Guest
            .Replace("  cores: 2\n", "")
            .Replace("    - { size: 32 }        # system disk\n", "    - { size: 32 }        # system disk\n  cpuType: host\n"), result);
    }

    [Fact]
    public void A_block_sequence_keeps_its_items_and_gains_new_ones_after_the_last()
    {
        var result = Edit(Guest, d => d["hardware"]!["disks"]!.AsArray().Add(new JsonObject { ["size"] = 100, ["datastore"] = "tank" }));
        Assert.Equal(Guest.Replace("    - { size: 32 }        # system disk\n",
            "    - { size: 32 }        # system disk\n    - size: 100\n      datastore: tank\n"), result);

        var shorter = Edit(result, d => d["hardware"]!["disks"]!.AsArray().RemoveAt(1));
        Assert.Equal(Guest, shorter);
    }

    [Fact]
    public void Strings_keep_their_quotes_and_those_that_would_read_as_something_else_are_quoted()
    {
        var result = Edit(Guest, d =>
        {
            d["inputs"]!["machine_git_user_name"] = "Ops' Person";
            d["inputs"]!["machine_t3_channel"] = "1.0";
            d["inputs"]!["machine_note"] = "yes";
            d["inputs"]!["machine_comment"] = "a # b";
        });
        Assert.Contains("  machine_git_user_name: 'Ops'' Person'\n", result);
        Assert.Contains("  machine_t3_channel: \"1.0\"\n  machine_note: yes\n  machine_comment: \"a # b\"\npolicy:", result);
    }

    [Fact]
    public void A_missing_section_is_added_in_block_style_at_the_end()
    {
        const string text = "schemaVersion: 1\nworkflow: test-vm@1\n";
        var result = Edit(text, d => d["inputs"] = new JsonObject
        {
            ["features"] = new JsonArray("a", "b"),
            ["tuning"] = new JsonObject { ["level"] = 3 },
            ["empty"] = new JsonObject(),
        });
        Assert.Equal("schemaVersion: 1\nworkflow: test-vm@1\ninputs:\n  features:\n    - a\n    - b\n  tuning:\n    level: 3\n  empty: {}\n", result);
    }

    [Fact]
    public void A_file_without_a_final_newline_gets_one_before_a_new_key()
    {
        Assert.Equal("schemaVersion: 1\nvmid: 2\n", Edit("schemaVersion: 1", d => d["vmid"] = 2));
    }

    [Fact]
    public void A_collection_emptied_or_changing_type_is_written_in_flow_style()
    {
        var emptied = Edit(Guest, d => d["inputs"] = new JsonObject());
        Assert.Contains("# Extra vars for setup.\ninputs: {}\npolicy: { approval: manual }\n", emptied);

        var flow = Edit("a:\nb: [1, 2]  # keep\n", d =>
        {
            d["a"] = new JsonObject { ["x"] = 1 };
            d["b"] = "two";
        });
        Assert.Equal("a: { x: 1 }\nb: two  # keep\n", flow);
    }

    [Fact]
    public void An_edit_through_an_alias_is_refused_rather_than_written_wrong()
    {
        const string text = "base: &disk { size: 32 }\nhardware:\n  disks:\n    - *disk\n";
        var desired = Read(text);
        desired["hardware"]!["disks"]![0]!["size"] = 64;
        Assert.Throws<YamlEditException>(() => YamlEditor.Apply(text, desired));
    }

    [Fact]
    public void A_file_that_is_not_a_mapping_is_refused()
    {
        Assert.Throws<YamlEditException>(() => YamlEditor.Apply("- a\n", new JsonObject()));
        Assert.Throws<YamlEditException>(() => YamlEditor.Apply("a: [\n", new JsonObject()));
    }
}
