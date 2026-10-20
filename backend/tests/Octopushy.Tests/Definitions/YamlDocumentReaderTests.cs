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
}
