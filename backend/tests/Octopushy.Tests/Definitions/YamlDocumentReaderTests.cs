using System.Text;
using System.Text.Json.Nodes;
using Octopushy.Definitions.Yaml;

namespace Octopushy.Tests.Definitions;

public class YamlDocumentReaderTests
{
    [Fact]
    public void Uses_the_yaml_1_2_core_schema()
    {
        var document = YamlDocumentReader.Read("""
            plainYes: yes
            on: on
            octal: 0400
            quoted: "0400"
            hex: 0x1F
            float: 1.5
            truth: true
            nothing: ~
            text: hello
            """);
        var root = (JsonObject)document.Root!;
        Assert.Equal("yes", root["plainYes"]!.GetValue<string>());
        Assert.Equal("on", root["on"]!.GetValue<string>());
        Assert.Equal(400, root["octal"]!.GetValue<long>());
        Assert.Equal("0400", root["quoted"]!.GetValue<string>());
        Assert.Equal(31, root["hex"]!.GetValue<long>());
        Assert.Equal(1.5, root["float"]!.GetValue<double>());
        Assert.True(root["truth"]!.GetValue<bool>());
        Assert.Null(root["nothing"]);
        Assert.Equal("hello", root["text"]!.GetValue<string>());
    }

    [Fact]
    public void Records_the_line_of_each_value()
    {
        var document = YamlDocumentReader.Read("a: 1\nb:\n  c: [1, 2]\n  d: x\n");
        Assert.Equal(1, document.LineOf("/a"));
        Assert.Equal(4, document.LineOf("/b/d"));
        Assert.Equal(3, document.LineOf("/b/c/1"));
        Assert.Equal(4, document.LineOf("/b/d/missing"));
    }

    [Fact]
    public void Refuses_duplicate_keys()
    {
        var error = Assert.Throws<YamlReadException>(() => YamlDocumentReader.Read("a: 1\nb: 2\na: 3\n"));
        Assert.Equal(3, error.Line);
        Assert.Contains("Duplicate key", error.Message);
    }

    [Fact]
    public void Refuses_several_documents()
    {
        Assert.Throws<YamlReadException>(() => YamlDocumentReader.Read("a: 1\n---\nb: 2\n"));
    }

    [Fact]
    public void Reports_syntax_errors_with_a_line()
    {
        var error = Assert.Throws<YamlReadException>(() => YamlDocumentReader.Read("a: 1\nb: c: d\n"));
        Assert.Equal(2, error.Line);
        Assert.Throws<YamlReadException>(() => YamlDocumentReader.Read("a: [1, 2\nb: 3\n"));
    }

    [Fact]
    public void Refuses_aliases_that_expand_past_the_node_limit()
    {
        // Six levels of ten aliases: a few hundred bytes that would expand to over a million values.
        var text = new StringBuilder("a0: &a0 [x, x, x, x, x, x, x, x, x, x]\n");
        for (var i = 1; i <= 6; i++)
            text.Append($"a{i}: &a{i} [{string.Join(", ", Enumerable.Repeat($"*a{i - 1}", 10))}]\n");

        var error = Assert.Throws<YamlReadException>(() => YamlDocumentReader.Read(text.ToString()));
        Assert.Contains($"more than {YamlDocumentReader.MaxNodes} values", error.Message);
    }

    [Fact]
    public void Refuses_aliases_that_expand_one_long_text_past_the_character_limit()
    {
        // Under a megabyte and 10,001 values as written, and about 9 GB of text once every alias is expanded.
        var text = new StringBuilder("big: &big \"").Append('x', 900_000).Append("\"\nall: [")
            .AppendJoin(", ", Enumerable.Repeat("*big", 10_000)).Append("]\n");
        Assert.True(text.Length <= YamlDocumentReader.MaxCharacters);

        var error = Assert.Throws<YamlReadException>(() => YamlDocumentReader.Read(text.ToString()));
        Assert.Contains($"more than {YamlDocumentReader.MaxExpandedCharacters} characters", error.Message);
    }

    [Fact]
    public void Refuses_one_long_key_above_many_values()
    {
        // No alias: each of the 10,000 values has a pointer that repeats the 900,000-character key above it. Written as an
        // explicit key, since YAML refuses a plain one of more than 1,024 characters.
        var text = new StringBuilder("? ").Append('k', 900_000).Append("\n: [").AppendJoin(", ", Enumerable.Repeat("0", 10_000)).Append("]\n");
        Assert.True(text.Length <= YamlDocumentReader.MaxCharacters);

        var error = Assert.Throws<YamlReadException>(() => YamlDocumentReader.Read(text.ToString()));
        Assert.Contains($"more than {YamlDocumentReader.MaxExpandedCharacters} characters", error.Message);
    }

    [Fact]
    public void Aliases_within_the_node_limit_still_expand()
    {
        var root = (JsonObject)YamlDocumentReader.Read("base: &base {cores: 2}\nvm: *base\n").Root!;
        Assert.Equal(2, root["vm"]!["cores"]!.GetValue<long>());
    }

    [Theory]
    [InlineData(65)]
    [InlineData(5_000)]
    [InlineData(500_000)]
    public void Refuses_nesting_past_the_depth_limit(int depth)
    {
        var text = new string('[', depth) + new string(']', depth);
        var error = Assert.Throws<YamlReadException>(() => YamlDocumentReader.Read(text));
        Assert.Contains($"deeper than {YamlDocumentReader.MaxDepth} levels", error.Message);
    }

    [Fact]
    public void Refuses_an_alias_that_nests_past_the_depth_limit()
    {
        // Each part is within the limit as written; the alias puts one inside the other.
        var deep = new string('[', YamlDocumentReader.MaxDepth - 1) + new string(']', YamlDocumentReader.MaxDepth - 1);
        var error = Assert.Throws<YamlReadException>(() => YamlDocumentReader.Read($"a: &a {deep}\nb: [[*a]]\n"));
        Assert.Contains($"deeper than {YamlDocumentReader.MaxDepth} levels", error.Message);
    }

    [Fact]
    public void Reads_nesting_at_the_depth_limit()
    {
        var depth = YamlDocumentReader.MaxDepth;
        Assert.NotNull(YamlDocumentReader.Read(new string('[', depth) + new string(']', depth)).Root);
    }

    [Fact]
    public void Refuses_a_file_past_the_size_limit()
    {
        var text = "a: \"" + new string('x', YamlDocumentReader.MaxCharacters) + "\"\n";
        var error = Assert.Throws<YamlReadException>(() => YamlDocumentReader.Read(text));
        Assert.Contains($"the limit on the size of a YAML file is {YamlDocumentReader.MaxCharacters}", error.Message);
    }

    [Fact]
    public void Refuses_a_self_referencing_alias()
    {
        Assert.Throws<YamlReadException>(() => YamlDocumentReader.Read("a: &a [1, *a]\n"));
    }
}
