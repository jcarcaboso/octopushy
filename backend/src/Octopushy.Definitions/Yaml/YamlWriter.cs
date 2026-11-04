using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Octopushy.Definitions.Yaml;

/// <summary>
/// Writes a new YAML definition as a person would: block mappings, short values and store references on one line in flow
/// style (<c>{ store: … }</c>, <c>- { size: 32 }</c>, <c>[a, b]</c>), and comments after the lines they explain, lined up.
/// What it writes is read back and compared with the values it was given, so it never writes something that reads otherwise.
/// </summary>
public static class YamlWriter
{
    /// <summary>The column comments start at, when the line is shorter.</summary>
    public const int CommentColumn = 34;

    private const int FlowLimit = 72;

    /// <summary>
    /// <paramref name="document"/> as YAML, in its key order. <paramref name="comments"/> are keyed by dotted path, such as
    /// <c>vmid</c> or <c>secrets.token</c>; <paramref name="header"/> lines are written first, each as a comment.
    /// </summary>
    /// <exception cref="YamlEditException">What was written does not read back as <paramref name="document"/>.</exception>
    public static string Write(JsonObject document, IReadOnlyDictionary<string, string>? comments = null, IEnumerable<string>? header = null)
    {
        var text = new StringBuilder();
        foreach (var line in header ?? [])
            text.Append(line.Length == 0 ? "#" : "# " + line).Append('\n');
        foreach (var (key, value) in document)
            Entry(text, key, value, 0, key, comments ?? new Dictionary<string, string>());
        var written = text.ToString();
        var read = YamlDocumentReader.Read(written).Root;
        if (!YamlEditor.Same(YamlEditor.Normalize(read), YamlEditor.Normalize(document)))
            throw new YamlEditException("The definition could not be written so that it reads back as the values given.");
        return written;
    }

    private static void Entry(StringBuilder text, string key, JsonNode? value, int indent, string path, IReadOnlyDictionary<string, string> comments)
    {
        var head = new string(' ', indent) + YamlEditor.RenderKey(key) + ":";
        comments.TryGetValue(path, out var comment);
        if (Inline(value) is { } inline)
        {
            Line(text, head + " " + inline, comment);
            return;
        }

        Line(text, head, comment);
        switch (value)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                    Entry(text, k, v, indent + 2, $"{path}.{k}", comments);
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    Item(text, a[i], indent + 2, $"{path}[{i}]", comments);
                break;
        }
    }

    private static void Item(StringBuilder text, JsonNode? item, int indent, string path, IReadOnlyDictionary<string, string> comments)
    {
        comments.TryGetValue(path, out var comment);
        var dash = new string(' ', indent) + "- ";
        if (Inline(item, inArray: true) is { } inline)
        {
            Line(text, dash + inline, comment);
            return;
        }

        // A mapping in a block sequence: its first key on the dash's line, the rest under it.
        var entries = new StringBuilder();
        var first = true;
        foreach (var (k, v) in item!.AsObject())
        {
            Entry(entries, k, v, indent + 2, $"{path}.{k}", comments);
            if (first)
            {
                entries.Remove(0, indent + 2).Insert(0, dash);
                first = false;
            }
        }

        text.Append(entries);
    }

    /// <summary>The value on one line, or null when it is written as a block.</summary>
    private static string? Inline(JsonNode? value, bool inArray = false)
    {
        switch (value)
        {
            case null or JsonValue:
                return YamlEditor.RenderScalar(value, null, flow: false);
            case JsonObject { Count: 0 }:
                return "{}";
            case JsonArray { Count: 0 }:
                return "[]";
            case JsonObject o when IsStoreReference(o) || inArray && o.All(p => p.Value is null or JsonValue):
                return Flow(o);
            case JsonArray a when a.All(i => i is null or JsonValue):
                return Flow(a);
            default:
                return null;
        }
    }

    private static string? Flow(JsonNode node)
    {
        var flow = YamlEditor.RenderFlow(node);
        return flow.Length <= FlowLimit ? flow : null;
    }

    /// <summary><c>{ store: key }</c>, with an optional <c>version</c>.</summary>
    private static bool IsStoreReference(JsonObject o) =>
        o.ContainsKey("store") && o.All(p => p.Key is "store" or "version") && o["store"]?.GetValueKind() == JsonValueKind.String;

    private static void Line(StringBuilder text, string content, string? comment)
    {
        text.Append(content);
        if (!string.IsNullOrWhiteSpace(comment))
            text.Append(' ', Math.Max(2, CommentColumn - content.Length)).Append("# ").Append(comment.ReplaceLineEndings(" ").Trim());
        text.Append('\n');
    }
}
