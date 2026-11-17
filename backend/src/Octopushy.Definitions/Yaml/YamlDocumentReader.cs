using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
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
/// <remarks>
/// Repository content is hostile, so what one file may expand to is bounded: its length, the values it holds once its
/// aliases are expanded, and how deep they nest. Going over one is a <see cref="YamlReadException"/> like any other.
/// </remarks>
public static partial class YamlDocumentReader
{
    /// <summary>The most characters a YAML file read here may hold.</summary>
    public const int MaxCharacters = 1024 * 1024;

    /// <summary>The most values a YAML file may hold, with each alias counted as often as it is used.</summary>
    public const int MaxNodes = 100_000;

    /// <summary>How deep its mappings and sequences may nest. The document's own value is depth 1.</summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// The most characters the file's keys and values, and the pointers kept to them, may hold together once its aliases
    /// are expanded.
    /// </summary>
    public const int MaxExpandedCharacters = 8 * MaxCharacters;

    public static YamlDocument Read(string text)
    {
        if (text.Length > MaxCharacters)
        {
            throw new YamlReadException(
                $"The file holds {text.Length} characters; the limit on the size of a YAML file is {MaxCharacters}.", null);
        }

        var stream = new YamlStream();
        try
        {
            // YamlDotNet builds nodes recursively, keeps every expanded tag, and formats a repeated key with its aliases
            // expanded. So depth, tags and keys are checked as the parser reads, before it builds anything from them.
            stream.Load(new BoundedParser(new Parser(new StringReader(text))));
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

        var (nodes, characters) = (0, 0L);
        var root = Convert(stream.Documents[0].RootNode, "", lines, 1, ref nodes, ref characters);

        // Round-tripping through text makes every value JSON-backed, so a number reads as int,
        // long, or double alike, the same as JSON parsed anywhere else.
        return new YamlDocument(root is null ? null : JsonNode.Parse(root.ToJsonString()), lines);
    }

    private static JsonNode? Convert(YamlNode node, string pointer, Dictionary<string, int> lines, int depth, ref int nodes, ref long characters)
    {
        // Counted as they are expanded: an alias used ten times is ten values, so a few nested aliases cannot become millions.
        if (++nodes > MaxNodes)
        {
            throw new YamlReadException(
                $"The file holds more than {MaxNodes} values once its aliases are expanded, the limit on values in a YAML file.", LineOf(node.Start));
        }

        if (depth > MaxDepth && node is YamlMappingNode or YamlSequenceNode)
        {
            throw TooDeep(node.Start);
        }

        // A pointer repeats every key above its value, so one long key over many values is long many times.
        Count(pointer, node, ref characters);
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

                    Count(key, keyNode, ref characters);
                    var childPointer = pointer + "/" + EscapePointer(key);
                    result[key] = Convert(valueNode, childPointer, lines, depth + 1, ref nodes, ref characters);
                }

                return result;
            }
            case YamlSequenceNode sequence:
            {
                var result = new JsonArray();
                var index = 0;
                foreach (var item in sequence.Children)
                {
                    result.Add(Convert(item, pointer + "/" + index.ToString(CultureInfo.InvariantCulture), lines, depth + 1, ref nodes, ref characters));
                    index++;
                }

                return result;
            }
            case YamlScalarNode scalar:
                Count(scalar.Value, scalar, ref characters);
                return ConvertScalar(scalar);
            default:
                throw new YamlReadException("Unsupported YAML node.", LineOf(node.Start));
        }
    }

    /// <summary>Counted as it is expanded, like the values: one long text behind an alias used a thousand times is a thousand texts.</summary>
    private static void Count(string? text, YamlNode node, ref long characters)
    {
        characters += text?.Length ?? 0;
        if (characters > MaxExpandedCharacters)
        {
            throw new YamlReadException(
                $"The file's keys, values and the paths to them hold more than {MaxExpandedCharacters} characters once its aliases are expanded, the limit on text in a YAML file.",
                LineOf(node.Start));
        }
    }

    /// <summary>A scalar read with the same rules as a document: quoted or <c>!!str</c> is text, plain may be a number, bool or null.</summary>
    internal static JsonNode? ConvertScalar(YamlScalarNode scalar)
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

    private static YamlReadException TooDeep(Mark mark) =>
        new($"The file nests deeper than {MaxDepth} levels, the limit on depth in a YAML file.", LineOf(mark));

    private sealed class BoundedParser(IParser inner) : IParser
    {
        private int _depth;
        private long _tagCharacters;

        // One entry per open mapping or sequence: whether a mapping's next node is a key, or null in a sequence.
        private readonly Stack<bool?> _open = new();

        // The anchors defined so far in this document, and whether each names a mapping or a sequence. An alias must
        // follow its anchor, as YAML requires: the library resolves one that comes first later, and recurses without
        // end when it is a key.
        private readonly Dictionary<string, bool> _anchors = new(StringComparer.Ordinal);

        public ParsingEvent? Current => inner.Current;

        public bool MoveNext()
        {
            if (!inner.MoveNext())
                return false;
            var current = inner.Current;
            if (current is DocumentStart)
            {
                _anchors.Clear();
            }

            var collection = current is MappingStart or SequenceStart;
            if (current is AnchorAlias alias)
            {
                if (!_anchors.TryGetValue(alias.Value.Value, out collection))
                {
                    throw new YamlReadException($"The alias '*{alias.Value.Value}' comes before its anchor.", LineOf(current.Start));
                }
            }

            if (current is NodeEvent or AnchorAlias && _open.TryPeek(out var key) && key is { } isKey)
            {
                if (isKey && collection)
                {
                    throw new YamlReadException("Mapping keys must be plain strings.", LineOf(current.Start));
                }

                _open.Pop();
                _open.Push(!isKey);
            }

            if (current is NodeEvent node)
            {
                // A %TAG prefix is written once and repeated in the tag of every node that uses its handle.
                if (!node.Tag.IsEmpty && (_tagCharacters += node.Tag.Value.Length) > MaxExpandedCharacters)
                {
                    throw new YamlReadException(
                        $"The file's tags hold more than {MaxExpandedCharacters} characters once their prefixes are expanded, the limit on text in a YAML file.",
                        LineOf(node.Start));
                }

                if (!node.Anchor.IsEmpty)
                {
                    _anchors[node.Anchor.Value] = collection;
                }
            }

            switch (current)
            {
                case MappingStart or SequenceStart when ++_depth > MaxDepth:
                    throw TooDeep(current.Start);
                case MappingStart:
                    _open.Push(true);
                    break;
                case SequenceStart:
                    _open.Push(null);
                    break;
                case MappingEnd or SequenceEnd:
                    _depth--;
                    _open.Pop();
                    break;
            }

            return true;
        }
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
