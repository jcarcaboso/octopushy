using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Octopushy.Definitions.Yaml;

/// <summary>Why a YAML file could not be rewritten to the values asked for without disturbing the rest of it.</summary>
public sealed class YamlEditException(string message) : Exception(message);

/// <summary>
/// Rewrites a YAML definition so it reads as new values while keeping everything else as it was written: comments, key order,
/// blank lines, quoting and flow or block style. Only the text of values that differ moves:
/// <list type="bullet">
/// <item>a changed scalar is replaced where it stands, in its own quoting style, so a comment after it stays;</item>
/// <item>a key removed from a block mapping takes its own lines with it, and a new key is added after the mapping's last
/// entry, at its indentation, in block style;</item>
/// <item>a block sequence keeps the items it still has, drops the ones at its end, and gains new ones after its last;</item>
/// <item>a flow collection (<c>{ a: 1 }</c>, <c>[a, b]</c>) whose keys or length change is written again in flow style, on
/// one line, in its original key order: comments inside one are lost;</item>
/// <item>a value whose type changes is written in flow style in place of the old one.</item>
/// </list>
/// The result is read back and compared with the values asked for, so an edit this cannot make reliably, such as one through
/// an anchor and its aliases, is refused rather than written wrong.
/// </summary>
public static class YamlEditor
{
    private sealed record Edit(int Start, int End, string Text);

    /// <summary>The text of <paramref name="text"/> rewritten to read as <paramref name="desired"/>.</summary>
    /// <exception cref="YamlEditException">The file is not one YAML mapping, or the edit cannot be made reliably.</exception>
    public static string Apply(string text, JsonObject desired)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (Exception e) when (e is YamlException or InvalidOperationException)
        {
            throw new YamlEditException($"The file is not valid YAML: {e.Message}");
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode { Style: not MappingStyle.Flow } root)
            throw new YamlEditException("The file is not one YAML block mapping.");
        JsonNode? current;
        try
        {
            current = YamlDocumentReader.Read(text).Root;
        }
        catch (YamlReadException e)
        {
            throw new YamlEditException($"The file cannot be read: {e.Message}");
        }

        desired = Normalize(desired)!.AsObject();
        var editor = new Editor(text);
        editor.EditMapping(root, current!.AsObject(), desired, isRoot: true);
        var result = editor.Result();
        JsonNode? reread;
        try
        {
            reread = YamlDocumentReader.Read(result).Root;
        }
        catch (YamlReadException e)
        {
            throw new YamlEditException($"The edited file would not be valid YAML ({e.Message}); edit it in the repository editor instead.");
        }

        if (!Same(reread, desired))
            throw new YamlEditException("The file cannot be edited reliably here, for example because it uses anchors and aliases; "
                + "edit it in the repository editor instead.");
        return result;
    }

    /// <summary>
    /// A copy of <paramref name="node"/> with every value JSON-backed. An explicit null stays a null: only a property that is
    /// absent is removed from the file.
    /// </summary>
    public static JsonNode? Normalize(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Normalize(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Normalize).ToArray()),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };

    /// <summary>Whether two values are the same JSON, numbers compared by value.</summary>
    public static bool Same(JsonNode? a, JsonNode? b)
    {
        switch (a, b)
        {
            case (null, null):
                return true;
            case (JsonObject x, JsonObject y):
                return x.Count == y.Count && x.All(p => y.TryGetPropertyValue(p.Key, out var other) && Same(p.Value, other));
            case (JsonArray x, JsonArray y):
                return x.Count == y.Count && x.Zip(y).All(p => Same(p.First, p.Second));
            case (JsonValue x, JsonValue y):
                var (kx, ky) = (x.GetValueKind(), y.GetValueKind());
                if (kx == JsonValueKind.Number && ky == JsonValueKind.Number)
                    return decimal.TryParse(x.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dx)
                        && decimal.TryParse(y.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dy)
                        ? dx == dy
                        : x.ToJsonString() == y.ToJsonString();
                return kx == ky && x.ToJsonString() == y.ToJsonString();
            default:
                return false;
        }
    }

    private sealed class Editor(string text)
    {
        private readonly List<Edit> _edits = [];

        public string Result()
        {
            var ordered = _edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].End > ordered[i - 1].Start)
                    throw new YamlEditException("The edit overlaps itself; edit the file in the repository editor instead.");
            }

            var builder = new StringBuilder(text);
            foreach (var edit in ordered)
                builder.Remove(edit.Start, edit.End - edit.Start).Insert(edit.Start, edit.Text);
            return builder.ToString();
        }

        /// <summary>Edits a value; <paramref name="key"/> is its key when it is the value of a block mapping entry.</summary>
        private void EditValue(YamlNode node, JsonNode? current, JsonNode? desired, YamlScalarNode? key)
        {
            if (Same(current, desired))
                return;
            switch (node)
            {
                case YamlScalarNode scalar when desired is JsonValue or null:
                    ReplaceScalar(scalar, desired);
                    return;
                case YamlMappingNode mapping when desired is JsonObject o && current is JsonObject c && (o.Count > 0 || c.Count == 0):
                    EditMapping(mapping, c, o, isRoot: false);
                    return;
                case YamlSequenceNode sequence when desired is JsonArray a && current is JsonArray c && (a.Count > 0 || c.Count == 0):
                    EditSequence(sequence, c, a);
                    return;
                default:
                    ReplaceWhole(node, desired, key);
                    return;
            }
        }

        public void EditMapping(YamlMappingNode mapping, JsonObject current, JsonObject desired, bool isRoot)
        {
            var children = mapping.Children.Select(c => ((YamlScalarNode)c.Key, c.Value)).ToList();
            var keys = children.Select(c => c.Item1.Value!).ToList();
            if (mapping.Style == MappingStyle.Flow)
            {
                if (keys.Count == desired.Count && keys.All(desired.ContainsKey))
                {
                    foreach (var (k, v) in children)
                        EditValue(v, current[k.Value!], desired[k.Value!], null);
                }
                else
                {
                    Replace(mapping.Start, FlowEnd(mapping), RenderFlow(Ordered(desired, keys)));
                }

                return;
            }

            // A mapping that is a sequence item starts on the item's line, after "- ": its first entry cannot lose its line.
            var firstOnOwnLine = OnlyIndentBefore(children[0].Item1.Start);
            if (!isRoot && !firstOnOwnLine && !desired.ContainsKey(keys[0]))
            {
                Replace(children[0].Item1.Start, LastEnd(children[^1].Value), RenderFlow(Ordered(desired, keys)));
                return;
            }

            var indent = (int)children[0].Item1.Start.Column - 1;
            foreach (var (k, v) in children)
            {
                if (!desired.TryGetPropertyValue(k.Value!, out var wanted))
                    Replace(LineStart(k.Start), LineEndAfter(LastEnd(v)), "");
                else
                    EditValue(v, current[k.Value!], wanted, k);
            }

            var added = desired.Where(p => !keys.Contains(p.Key)).ToList();
            if (added.Count == 0)
                return;
            var at = LineEndAfter(LastEnd(children[^1].Value));
            var block = new StringBuilder();
            if (at == text.Length && text.Length > 0 && text[^1] != '\n')
                block.Append('\n');
            foreach (var (k, v) in added)
                AppendEntry(block, k, v, indent);
            Replace(at, at, block.ToString());
        }

        private void EditSequence(YamlSequenceNode sequence, JsonArray current, JsonArray desired)
        {
            var items = sequence.Children.ToList();
            if (sequence.Style == SequenceStyle.Flow)
            {
                if (items.Count == desired.Count)
                {
                    for (var i = 0; i < items.Count; i++)
                        EditValue(items[i], current[i], desired[i], null);
                }
                else
                {
                    Replace(sequence.Start, FlowEnd(sequence), RenderFlow(desired));
                }

                return;
            }

            var common = Math.Min(items.Count, desired.Count);
            for (var i = 0; i < common; i++)
                EditValue(items[i], current[i], desired[i], null);
            if (items.Count > desired.Count)
            {
                Replace(LineStart(items[desired.Count].Start), LineEndAfter(LastEnd(items[^1])), "");
                return;
            }

            if (desired.Count == items.Count)
                return;
            var indent = (int)sequence.Start.Column - 1;
            var at = LineEndAfter(LastEnd(items[^1]));
            var block = new StringBuilder();
            if (at == text.Length && text.Length > 0 && text[^1] != '\n')
                block.Append('\n');
            for (var i = items.Count; i < desired.Count; i++)
                AppendItem(block, desired[i], indent);
            Replace(at, at, block.ToString());
        }

        private void ReplaceScalar(YamlScalarNode scalar, JsonNode? desired)
        {
            var start = (int)scalar.Start.Index;
            var end = (int)scalar.End.Index;
            var rendered = RenderScalar(desired, scalar.Style, flow: InFlow(start));
            if (scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded)
            {
                // A block scalar runs to the end of its last line; what replaces it ends its line the same way.
                Replace(start, end, rendered + (end > start && text[end - 1] == '\n' ? "\n" : ""));
                return;
            }

            // An empty value, "key:", needs the space a value takes after the colon.
            if (start == end && start > 0 && text[start - 1] == ':')
                rendered = " " + rendered;
            Replace(start, end, rendered);
        }

        private void ReplaceWhole(YamlNode node, JsonNode? desired, YamlScalarNode? key)
        {
            var rendered = desired is JsonObject or JsonArray ? RenderFlow(desired) : RenderScalar(desired, null, InFlow((int)node.Start.Index));
            switch (node)
            {
                case YamlScalarNode scalar when desired is not (JsonObject or JsonArray):
                    ReplaceScalar(scalar, desired);
                    return;
                case YamlScalarNode scalar:
                {
                    var start = (int)scalar.Start.Index;
                    var end = (int)scalar.End.Index;
                    if (scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded)
                        end = TrimLineEnd(end);
                    else if (start == end && start > 0 && text[start - 1] == ':')
                        rendered = " " + rendered;
                    Replace(start, end, rendered);
                    return;
                }
                case YamlMappingNode { Style: MappingStyle.Flow }:
                case YamlSequenceNode { Style: SequenceStyle.Flow }:
                    Replace((int)node.Start.Index, FlowEnd(node), rendered);
                    return;
                default:
                    // A block collection: from just after its key's colon, or where its sequence item starts, to its end.
                    var last = LastEnd(node);
                    if (key is not null)
                    {
                        var colon = text.IndexOf(':', (int)key.End.Index);
                        Replace(colon + 1, TrimLineEnd(last), " " + rendered);
                    }
                    else
                    {
                        var start = node is YamlSequenceNode ? (int)node.Start.Index : (int)((YamlMappingNode)node).Children[0].Key.Start.Index;
                        if (!OnlyIndentBefore(node is YamlSequenceNode ? node.Start : ((YamlMappingNode)node).Children[0].Key.Start))
                            Replace(start, TrimLineEnd(last), rendered);
                        else
                            throw new YamlEditException("A nested block collection changes type here; edit the file in the repository editor instead.");
                    }

                    return;
            }
        }

        private void Replace(Mark start, int end, string replacement) => Replace((int)start.Index, end, replacement);

        private void Replace(int start, int end, string replacement) => _edits.Add(new Edit(start, end, replacement));

        /// <summary>Where a node's text ends: after a scalar, after a flow collection's closing bracket, or after a block collection's last value.</summary>
        private int LastEnd(YamlNode node) => node switch
        {
            YamlScalarNode s => (int)s.End.Index,
            YamlMappingNode { Style: MappingStyle.Flow } or YamlSequenceNode { Style: SequenceStyle.Flow } => FlowEnd(node),
            YamlMappingNode m => m.Children.Count == 0 ? (int)m.Start.Index : LastEnd(m.Children[^1].Value),
            YamlSequenceNode q => q.Children.Count == 0 ? (int)q.Start.Index : LastEnd(q.Children[^1]),
            _ => throw new YamlEditException("Unsupported YAML node."),
        };

        /// <summary>Just past the bracket that closes a flow collection.</summary>
        private int FlowEnd(YamlNode node)
        {
            var close = node is YamlMappingNode ? '}' : ']';
            var from = node switch
            {
                YamlMappingNode { Children.Count: > 0 } m => LastEnd(m.Children[^1].Value),
                YamlSequenceNode { Children.Count: > 0 } q => LastEnd(q.Children[^1]),
                _ => (int)node.Start.Index + 1,
            };
            for (var i = from; i < text.Length; i++)
            {
                if (text[i] == close)
                    return i + 1;
                if (text[i] == '#')
                {
                    var newline = text.IndexOf('\n', i);
                    if (newline < 0)
                        break;
                    i = newline;
                    continue;
                }

                if (!char.IsWhiteSpace(text[i]) && text[i] != ',')
                    break;
            }

            throw new YamlEditException("A flow collection's end could not be found.");
        }

        private int LineStart(Mark mark)
        {
            var index = (int)mark.Index;
            return index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        }

        /// <summary>The start of the line after the one <paramref name="end"/> is on, or the end of the text.</summary>
        private int LineEndAfter(int end)
        {
            if (end > 0 && text[end - 1] == '\n')
                return end;
            var newline = text.IndexOf('\n', end);
            return newline < 0 ? text.Length : newline + 1;
        }

        /// <summary>The end of a value without the line break a block scalar ends with.</summary>
        private int TrimLineEnd(int end) => end > 0 && text[end - 1] == '\n' ? end - 1 : end;

        private bool OnlyIndentBefore(Mark mark)
        {
            var start = LineStart(mark);
            return text.AsSpan(start, (int)mark.Index - start).IsWhiteSpace();
        }

        /// <summary>Whether <paramref name="index"/> is inside a flow collection, where commas and brackets end a plain scalar.</summary>
        private bool InFlow(int index)
        {
            var depth = 0;
            var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
            for (var i = start; i < index; i++)
            {
                if (text[i] is '{' or '[')
                    depth++;
                else if (text[i] is '}' or ']')
                    depth--;
            }

            return depth > 0;
        }
    }

    /// <summary><paramref name="desired"/>'s properties, those also in <paramref name="original"/> first, in its order.</summary>
    private static JsonObject Ordered(JsonObject desired, IReadOnlyList<string> original)
    {
        var ordered = new JsonObject();
        foreach (var key in original.Where(desired.ContainsKey))
            ordered[key] = desired[key]?.DeepClone();
        foreach (var (key, value) in desired.Where(p => !original.Contains(p.Key)))
            ordered[key] = value?.DeepClone();
        return ordered;
    }

    private static void AppendEntry(StringBuilder block, string key, JsonNode? value, int indent)
    {
        block.Append(' ', indent).Append(RenderKey(key)).Append(':');
        switch (value)
        {
            case JsonObject { Count: > 0 } o:
                block.Append('\n');
                foreach (var (k, v) in o)
                    AppendEntry(block, k, v, indent + 2);
                break;
            case JsonArray { Count: > 0 } a:
                block.Append('\n');
                foreach (var item in a)
                    AppendItem(block, item, indent + 2);
                break;
            default:
                block.Append(' ').Append(value is JsonObject or JsonArray ? RenderFlow(value) : RenderScalar(value, null, flow: false)).Append('\n');
                break;
        }
    }

    private static void AppendItem(StringBuilder block, JsonNode? item, int indent)
    {
        block.Append(' ', indent).Append("- ");
        if (item is JsonObject { Count: > 0 } o)
        {
            var first = true;
            foreach (var (k, v) in o)
            {
                var entry = new StringBuilder();
                AppendEntry(entry, k, v, first ? 0 : indent + 2);
                block.Append(entry);
                first = false;
            }

            return;
        }

        block.Append(item is JsonObject or JsonArray ? RenderFlow(item) : RenderScalar(item, null, flow: false)).Append('\n');
    }

    /// <summary>A value on one line in flow style: <c>{ a: 1, b: [x, y] }</c>.</summary>
    private static string RenderFlow(JsonNode? node) => node switch
    {
        JsonObject { Count: 0 } => "{}",
        JsonObject o => "{ " + string.Join(", ", o.Select(p => $"{RenderKey(p.Key)}: {RenderFlow(p.Value)}")) + " }",
        JsonArray { Count: 0 } => "[]",
        JsonArray a => "[" + string.Join(", ", a.Select(RenderFlow)) + "]",
        _ => RenderScalar(node, null, flow: true),
    };

    private static string RenderKey(string key) => PlainReadsAs(key, flow: true) ? key : DoubleQuoted(key);

    private static string RenderScalar(JsonNode? value, ScalarStyle? style, bool flow)
    {
        if (value is null)
            return "null";
        var kind = value.GetValueKind();
        if (kind != JsonValueKind.String)
            return kind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => value.ToJsonString(),
            };
        var text = value.GetValue<string>();
        return style switch
        {
            ScalarStyle.SingleQuoted when !text.Any(char.IsControl) => "'" + text.Replace("'", "''") + "'",
            ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted => DoubleQuoted(text),
            _ when PlainReadsAs(text, flow) => text,
            _ => DoubleQuoted(text),
        };
    }

    /// <summary>Whether <paramref name="text"/> written plain reads back as exactly that string.</summary>
    private static bool PlainReadsAs(string text, bool flow)
    {
        if (text.Length == 0 || text != text.Trim() || text.Any(char.IsControl)
            || "-?:,[]{}#&*!|>'\"%@`".Contains(text[0]) || text.Contains(": ") || text.EndsWith(':') || text.Contains(" #")
            || flow && text.IndexOfAny([',', '[', ']', '{', '}']) >= 0)
            return false;
        try
        {
            var read = YamlDocumentReader.Read(flow ? $"k: [{text}]" : $"k: {text}").Root?["k"];
            var value = flow ? (read as JsonArray) is { Count: 1 } array ? array[0] : null : read;
            return value is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == text;
        }
        catch (YamlReadException)
        {
            return false;
        }
    }

    private static string DoubleQuoted(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\t' => "\\t",
                '\r' => "\\r",
                _ when char.IsControl(c) => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }
}
