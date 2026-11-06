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
public static partial class OutputMasking
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

    /// <summary>Why outputs were withheld because a number in them is not written as the database keeps it.</summary>
    public const string NumberForm =
        "[withheld: a number in what this attempt returned is not written as the database keeps it, with an exponent or as negative zero, so Octopushy cannot check it for secrets and does not keep it]";

    /// <summary>
    /// The output with its strings masked, or false with the reason it is withheld whole. The work is linear in the size of
    /// the output for each secret, apart from ordering each object's keys as jsonb does.
    /// </summary>
    public static bool TryMask(string json, Secrets secrets, out JsonNode? masked, out string? withheld)
    {
        masked = null;
        try
        {
            var root = JsonNode.Parse(json);
            if (root is JsonValue { } value && value.GetValueKind() == JsonValueKind.String)
                root = JsonValue.Create(Replace(secrets.Values, value.GetValue<string>()));
            withheld = Walk(root, secrets, mask: true) ?? AcrossTokens(root, secrets);
            if (withheld is not null)
                return false;
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

    /// <summary>
    /// Why a document assembled from masked outputs may not be kept or shown, or null when it may: the same rules as for
    /// outputs, and a secret in a string is a reason too, since what was added after masking is not masked. Nothing is
    /// rewritten, so a digest or an identity in it is never changed: the document is kept as it is, or not at all.
    /// </summary>
    public static string? Check(JsonNode? document, Secrets secrets)
    {
        try
        {
            return Walk(document, secrets, mask: false) ?? AcrossTokens(document, secrets);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return NotJson;
        }
    }

    /// <summary>A secret that spans a name and its value, two values, or punctuation, in any text the document is kept or shown as.</summary>
    private static string? AcrossTokens(JsonNode? root, Secrets secrets) =>
        secrets.Values.Length > 0 && Texts(root).Any(text => secrets.Values.Any(s => text.Contains(s, StringComparison.Ordinal)))
            ? SecretOutsideStrings
            : null;

    /// <summary>
    /// Masks every string below <paramref name="node"/> in place, or with <paramref name="mask"/> false refuses one that holds
    /// a secret; returns why the document is withheld when a secret is anywhere else.
    /// </summary>
    private static string? Walk(JsonNode? node, Secrets secrets, bool mask)
    {
        switch (node)
        {
            case null:
                return IsSecretLiteral("null", secrets) ? SecretOutsideStrings : null;
            case JsonObject o:
                if (IsSecretDocument(o, secrets))
                    return SecretOutsideStrings;
                foreach (var (name, child) in o.ToList())
                {
                    if (Holds(name, secrets))
                        return SecretOutsideStrings;
                    if (child is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                    {
                        if (!mask && Holds(v.GetValue<string>(), secrets))
                            return SecretOutsideStrings;
                        if (mask)
                            o[name] = JsonValue.Create(Replace(secrets.Values, v.GetValue<string>()));
                    }
                    else if (Walk(child, secrets, mask) is { } why)
                        return why;
                }

                return null;
            case JsonArray a:
                if (IsSecretDocument(a, secrets))
                    return SecretOutsideStrings;
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                    {
                        if (!mask && Holds(v.GetValue<string>(), secrets))
                            return SecretOutsideStrings;
                        if (mask)
                            a[i] = JsonValue.Create(Replace(secrets.Values, v.GetValue<string>()));
                    }
                    else if (Walk(a[i], secrets, mask) is { } why)
                        return why;
                }

                return null;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                return !mask && Holds(v.GetValue<string>(), secrets) ? SecretOutsideStrings : null;
            default:
                // A number, true or false. A number is checked as the database prints it, which is as it is written only
                // in the plain form: anything else is not kept, rather than predicted.
                var text = node.ToJsonString();
                if (node.GetValueKind() == JsonValueKind.Number && secrets.Values.Length > 0 && !IsPlainNumber(text))
                    return NumberForm;
                return IsSecretLiteral(text, secrets) ? SecretOutsideStrings : null;
        }
    }

    private static bool Holds(string text, Secrets secrets) => secrets.Values.Any(s => text.Contains(s, StringComparison.Ordinal));

    private static bool IsSecretLiteral(string text, Secrets secrets) => secrets.Values.Contains(text, StringComparer.Ordinal);

    private static bool IsSecretDocument(JsonNode node, Secrets secrets) => secrets.Documents.Any(d => JsonNode.DeepEquals(node, d));

    /// <summary>
    /// Whether PostgreSQL prints a JSON number exactly as written: digits with an optional fraction, no exponent, and not a
    /// negative zero, which it prints without its sign.
    /// </summary>
    private static bool IsPlainNumber(string text) => PlainNumber().IsMatch(text) && !NegativeZero().IsMatch(text);

    [System.Text.RegularExpressions.GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$")]
    private static partial System.Text.RegularExpressions.Regex PlainNumber();

    [System.Text.RegularExpressions.GeneratedRegex(@"^-0(\.0+)?$")]
    private static partial System.Text.RegularExpressions.Regex NegativeZero();

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
                // Only plain numbers are kept, and jsonb prints those as written.
                text.Append(node.ToJsonString());
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
