using System.Text.Json.Nodes;
using Octopushy.Definitions.Yaml;

namespace Octopushy.Tests.Definitions;

/// <summary>A new definition is written as a person would write it, and always reads back as the values it was given.</summary>
public class YamlWriterTests
{
    [Fact]
    public void A_new_definition_is_block_style_with_short_values_inline_and_comments_lined_up()
    {
        var document = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["vmid"] = 311,
            ["hardware"] = new JsonObject { ["cores"] = 2, ["disks"] = new JsonArray(new JsonObject { ["size"] = 32 }) },
            ["inputs"] = new JsonObject
            {
                ["labels"] = new JsonArray("home", "ci"),
                ["note"] = "yes",
                ["colon"] = "a: b",
                ["empty"] = new JsonObject(),
                ["nested"] = new JsonArray(new JsonObject { ["name"] = "x", ["ports"] = new JsonArray(80, 443) }),
            },
            ["secrets"] = new JsonObject { ["token"] = new JsonObject { ["store"] = "guests/a/token" } },
        };
        var text = YamlWriter.Write(document, new Dictionary<string, string> { ["vmid"] = "free", ["secrets.token"] = "generated" }, ["Header."]);
        Assert.Equal("""
            # Header.
            schemaVersion: 1
            vmid: 311                         # free
            hardware:
              cores: 2
              disks:
                - { size: 32 }
            inputs:
              labels: [home, ci]
              note: yes
              colon: "a: b"
              empty: {}
              nested:
                - name: x
                  ports: [80, 443]
            secrets:
              token: { store: guests/a/token }  # generated

            """.ReplaceLineEndings("\n"), text);
        Assert.True(YamlEditor.Same(YamlDocumentReader.Read(text).Root, document));
    }

    [Fact]
    public void A_sequence_in_a_sequence_too_long_for_one_line_is_written_as_a_block()
    {
        const string Low = "2001:db8:aaaa:bbbb:cccc:dddd:eeee:0001";
        const string High = "2001:db8:aaaa:bbbb:cccc:dddd:eeee:ffff";
        var document = new JsonObject
        {
            ["inputs"] = new JsonObject
            {
                ["ranges"] = new JsonArray(new JsonArray(Low, High), new JsonArray("a", "b")),
                ["deep"] = new JsonArray(new JsonArray(new JsonArray(Low, High, Low))),
            },
        };
        var text = YamlWriter.Write(document);
        Assert.Equal($"""
            inputs:
              ranges:
                - - {Low}
                  - {High}
                - [a, b]
              deep:
                - - - {Low}
                    - {High}
                    - {Low}

            """.ReplaceLineEndings("\n"), text);
        Assert.True(YamlEditor.Same(YamlDocumentReader.Read(text).Root, document));
    }
}
