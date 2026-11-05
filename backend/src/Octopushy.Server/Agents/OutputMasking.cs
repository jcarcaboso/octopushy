using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Octopushy.Server.Agents;

/// <summary>
/// Masks what an attempt returned as JSON: secrets in strings become <c>***</c>, and the strings stay strings, so what reads
/// them keeps working. Nothing else is rewritten. Where a secret is anywhere a string replacement cannot reach, the whole
/// output is withheld instead: in a property name, as the text of a number or a literal, as a whole JSON value, or across
/// tokens in a text the output is kept or shown as.
/// </summary>
public static class OutputMasking
{
    /// <summary>Why outputs were withheld because of where a secret is in them.</summary>
    public const string SecretOutsideStrings =
        "[withheld: what this attempt returned holds a secret where it cannot be masked, outside a string, so Octopushy does not keep it]";

    /// <summary>Why outputs that are not JSON were not kept.</summary>
    public const string NotJson = "[withheld: what this attempt returned is not JSON this server reads, so Octopushy does not keep it]";

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The secrets to mask with, longest first, and those that are themselves JSON objects or arrays.</summary>
    public sealed record Secrets(string[] Values, JsonNode[] Documents)
    {
        public static Secrets Of(string[] values) => new(values, values.Select(Document).OfType<JsonNode>().ToArray());

        private static JsonNode? Document(string value)
        {
            var text = value.TrimStart();
            if (text.Length == 0 || text[0] is not ('{' or '['))
                return null;
            try
            {
                return JsonNode.Parse(value) is { } node and (JsonObject or JsonArray) ? node : null;
            }
            catch (Exception e) when (e is JsonException or ArgumentException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The output with its strings masked, or false with the reason it is withheld whole. The work is linear in the size of
    /// the output for each secret.
    /// </summary>
    public static bool TryMask(string json, Secrets secrets, out JsonNode? masked, out string? withheld)
    {
        masked = null;
        withheld = null;
        try
        {
            var root = JsonNode.Parse(json);
            if (root is JsonValue { } value && value.GetValueKind() == JsonValueKind.String)
                root = JsonValue.Create(Replace(secrets.Values, value.GetValue<string>()));
            else if (!Walk(root, secrets))
            {
                withheld = SecretOutsideStrings;
                return false;
            }

            // Across tokens: a secret that spans a name and its value, two values, or a value and the punctuation between, in
            // any text this output is kept or shown as.
            foreach (var text in Texts(root))
            {
                if (secrets.Values.Any(s => text.Contains(s, StringComparison.Ordinal)))
                {
                    withheld = SecretOutsideStrings;
                    return false;
                }
            }

            masked = root;
            return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            // Not JSON, nested too deep, or a property named twice.
            withheld = NotJson;
            return false;
        }
    }

    /// <summary>Masks every string below <paramref name="node"/> in place; false when a secret is anywhere else.</summary>
    private static bool Walk(JsonNode? node, Secrets secrets)
    {
        switch (node)
        {
            case null:
                return !IsSecretLiteral("null", secrets);
            case JsonObject o:
                if (IsSecretDocument(o, secrets))
                    return false;
                foreach (var (name, child) in o.ToList())
                {
                    if (secrets.Values.Any(s => name.Contains(s, StringComparison.Ordinal)))
                        return false;
                    if (child is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                        o[name] = JsonValue.Create(Replace(secrets.Values, v.GetValue<string>()));
                    else if (!Walk(child, secrets))
                        return false;
                }

                return true;
            case JsonArray a:
                if (IsSecretDocument(a, secrets))
                    return false;
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                        a[i] = JsonValue.Create(Replace(secrets.Values, v.GetValue<string>()));
                    else if (!Walk(a[i], secrets))
                        return false;
                }

                return true;
            default:
                // A number, true or false: as it was written, and as a database normalizes a number.
                var text = node.ToJsonString();
                return !IsSecretLiteral(text, secrets) && !(Normalized(text) is { } number && IsSecretLiteral(number, secrets));
        }
    }

    private static bool IsSecretLiteral(string text, Secrets secrets) => secrets.Values.Contains(text, StringComparer.Ordinal);

    private static bool IsSecretDocument(JsonNode node, Secrets secrets) => secrets.Documents.Any(d => JsonNode.DeepEquals(node, d));

    private static string? Normalized(string number) =>
        decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>The texts an output is kept or shown as: compact as this server writes it, with and without escaping, and jsonb.</summary>
    private static IEnumerable<string> Texts(JsonNode? root)
    {
        yield return root?.ToJsonString() ?? "null";
        yield return root?.ToJsonString(Relaxed) ?? "null";
        var jsonb = new StringBuilder();
        Jsonb(root, jsonb);
        yield return jsonb.ToString();
    }

    /// <summary>
    /// PostgreSQL's jsonb text: <c>": "</c> and <c>", "</c> between tokens, keys shorter first and then by their bytes, only
    /// what JSON requires escaped, and numbers as numeric prints them.
    /// </summary>
    private static void Jsonb(JsonNode? node, StringBuilder text)
    {
        switch (node)
        {
            case null:
                text.Append("null");
                break;
            case JsonObject o:
                text.Append('{');
                var first = true;
                foreach (var (name, child) in o.OrderBy(p => Encoding.UTF8.GetByteCount(p.Key)).ThenBy(p => p.Key, Utf8Order.Instance))
                {
                    text.Append(first ? "" : ", ");
                    first = false;
                    Quote(name, text);
                    text.Append(": ");
                    Jsonb(child, text);
                }

                text.Append('}');
                break;
            case JsonArray a:
                text.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    text.Append(i == 0 ? "" : ", ");
                    Jsonb(a[i], text);
                }

                text.Append(']');
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                Quote(v.GetValue<string>(), text);
                break;
            default:
                var raw = node.ToJsonString();
                text.Append(Normalized(raw) ?? raw);
                break;
        }
    }

    private static void Quote(string value, StringBuilder text)
    {
        text.Append('"');
        foreach (var c in value)
        {
            _ = c switch
            {
                '"' => text.Append("\\\""),
                '\\' => text.Append("\\\\"),
                '\b' => text.Append("\\b"),
                '\f' => text.Append("\\f"),
                '\n' => text.Append("\\n"),
                '\r' => text.Append("\\r"),
                '\t' => text.Append("\\t"),
                < ' ' => text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)),
                _ => text.Append(c),
            };
        }

        text.Append('"');
    }

    /// <summary>Replaces each secret in <paramref name="text"/> with <c>***</c>, longest first.</summary>
    public static string Replace(string[] values, string text)
    {
        foreach (var value in values)
        {
            text = text.Replace(value, "***", StringComparison.Ordinal);
        }

        return text;
    }

    private sealed class Utf8Order : IComparer<string>
    {
        public static readonly Utf8Order Instance = new();

        public int Compare(string? x, string? y) => Encoding.UTF8.GetBytes(x ?? "").AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y ?? ""));
    }
}
