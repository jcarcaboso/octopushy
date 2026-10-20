using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Octopushy.Definitions.Yaml;

/// <summary>A YAML file read as JSON, with the line of every value for error messages.</summary>
public sealed class YamlDocument
{
    internal YamlDocument(JsonNode? root, IReadOnlyDictionary<string, int> lines)
    {
        Root = root;
        Lines = lines;
    }

    public JsonNode? Root { get; }

    /// <summary>One-based line numbers keyed by JSON pointer ("" is the document).</summary>
    public IReadOnlyDictionary<string, int> Lines { get; }

    /// <summary>The line of the value at <paramref name="pointer"/>, or of its nearest parent.</summary>
    public int? LineOf(string pointer)
    {
        var current = pointer;
        while (true)
        {
            if (Lines.TryGetValue(current, out var line))
            {
                return line;
            }

            if (current.Length == 0)
            {
                return null;
            }

            var slash = current.LastIndexOf('/');
            current = slash <= 0 ? "" : current[..slash];
        }
    }
}

public sealed class YamlReadException(string message, int? line) : Exception(message)
{
    public int? Line { get; } = line;
}

/// <summary>
/// Reads YAML with the YAML 1.2 core schema: unquoted <c>yes</c> and <c>on</c> stay strings, and
/// <c>0400</c> is the number 400. Anchors and aliases resolve; duplicate keys and several
/// documents in one file are errors.
/// </summary>
public static partial class YamlDocumentReader
{
    public static YamlDocument Read(string text)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException e)
        {
            throw new YamlReadException(CleanMessage(e.Message), LineOf(e.Start));
        }
        catch (InvalidOperationException)
        {
            // YamlDotNet reports some malformed documents, such as an unclosed flow sequence, this way.
            throw new YamlReadException("The YAML is malformed; check brackets and indentation.", null);
        }

        if (stream.Documents.Count > 1)
        {
            throw new YamlReadException("A definition file holds one YAML document.", LineOf(stream.Documents[1].RootNode.Start));
        }

        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        if (stream.Documents.Count == 0)
        {
            return new YamlDocument(null, lines);
        }

        var root = Convert(stream.Documents[0].RootNode, "", lines);

        // Round-tripping through text makes every value JSON-backed, so a number reads as int,
        // long, or double alike, the same as JSON parsed anywhere else.
        return new YamlDocument(root is null ? null : JsonNode.Parse(root.ToJsonString()), lines);
    }

    private static JsonNode? Convert(YamlNode node, string pointer, Dictionary<string, int> lines)
    {
        lines[pointer] = LineOf(node.Start) ?? 1;
        switch (node)
        {
            case YamlMappingNode mapping:
            {
                var result = new JsonObject();
                foreach (var (keyNode, valueNode) in mapping.Children)
                {
                    if (keyNode is not YamlScalarNode { Value: { } key })
                    {
                        throw new YamlReadException("Mapping keys must be plain strings.", LineOf(keyNode.Start));
                    }

                    if (result.ContainsKey(key))
                    {
                        throw new YamlReadException($"The key '{key}' appears twice.", LineOf(keyNode.Start));
                    }

                    var childPointer = pointer + "/" + EscapePointer(key);
                    result[key] = Convert(valueNode, childPointer, lines);
                }

                return result;
            }
            case YamlSequenceNode sequence:
            {
                var result = new JsonArray();
                var index = 0;
                foreach (var item in sequence.Children)
                {
                    result.Add(Convert(item, pointer + "/" + index.ToString(CultureInfo.InvariantCulture), lines));
                    index++;
                }

                return result;
            }
            case YamlScalarNode scalar:
                return ConvertScalar(scalar);
            default:
                throw new YamlReadException("Unsupported YAML node.", LineOf(node.Start));
        }
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        var tag = scalar.Tag.IsEmpty ? null : scalar.Tag.Value;
        if (tag is "tag:yaml.org,2002:str" || scalar.Style is not ScalarStyle.Plain)
        {
            return JsonValue.Create(value);
        }

        if (tag is not null and not "?")
        {
            throw new YamlReadException($"The YAML tag '{tag}' is not supported.", LineOf(scalar.Start));
        }

        if (value is "" or "~" or "null" or "Null" or "NULL")
        {
            return null;
        }

        if (value is "true" or "True" or "TRUE")
        {
            return JsonValue.Create(true);
        }

        if (value is "false" or "False" or "FALSE")
        {
            return JsonValue.Create(false);
        }

        if (DecimalInteger().IsMatch(value))
        {
            if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            {
                return JsonValue.Create(number);
            }

            return JsonValue.Create(decimal.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
        }

        if (value.StartsWith("0o", StringComparison.Ordinal) && OctalDigits().IsMatch(value[2..]))
        {
            return JsonValue.Create(System.Convert.ToInt64(value[2..], 8));
        }

        if (value.StartsWith("0x", StringComparison.Ordinal) && HexDigits().IsMatch(value[2..]))
        {
            return JsonValue.Create(long.Parse(value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
        }

        if (Float().IsMatch(value))
        {
            return JsonValue.Create(double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        if (value is ".inf" or ".Inf" or ".INF" or "+.inf" or "-.inf" or ".nan" or ".NaN" or ".NAN")
        {
            throw new YamlReadException($"'{value}' has no JSON equivalent; quote it if it is meant as text.", LineOf(scalar.Start));
        }

        return JsonValue.Create(value);
    }

    internal static string EscapePointer(string segment) => segment.Replace("~", "~0").Replace("/", "~1");

    private static int? LineOf(Mark mark) => mark.Line > 0 ? (int)mark.Line : null;

    private static string CleanMessage(string message)
    {
        // YamlDotNet prefixes positions like "(Line: 3, Col: 5, Idx: 20) - (Line: ...): "; the
        // line is reported separately.
        var index = message.IndexOf("): ", StringComparison.Ordinal);
        return index >= 0 && message.StartsWith('(') ? message[(index + 3)..] : message;
    }

    [GeneratedRegex("^[-+]?[0-9]+$")]
    private static partial Regex DecimalInteger();

    [GeneratedRegex("^[0-7]+$")]
    private static partial Regex OctalDigits();

    [GeneratedRegex("^[0-9a-fA-F]+$")]
    private static partial Regex HexDigits();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex Float();
}
