using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Api;

/// <summary>
/// What a run changes, as a list of runs shows it: the prepared values of its previews against the guest's last successful
/// run, the VM settings and resources its plans change, and its compose images. Stored on the run, as
/// <see cref="Run.Changes"/>, whenever what it is made from changes, because the lists never load a run's context or a
/// finished run's step outputs. Every value here is repository content or tool output: nothing in it may throw, and the
/// list and each of its strings are capped.
/// </summary>
public static partial class RunChanges
{
    /// <summary>A run lists at most this many changes.</summary>
    public const int MaxLines = 20;

    /// <summary>A name or value longer than this is cut, ending in an ellipsis.</summary>
    public const int MaxText = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Recomputes <see cref="Run.Changes"/> from the run's context and its loaded steps.</summary>
    public static void Refresh(Run run)
    {
        RunContext? context;
        try
        {
            context = RunContext.Parse(run.Context);
        }
        catch (Exception)
        {
            context = null;
        }

        run.Changes = Serialize(Of(context, run.Steps));
    }

    /// <summary>The column's text for <paramref name="changes"/>; null when the run compared nothing.</summary>
    public static string? Serialize(IReadOnlyList<RunChangeDto>? changes) =>
        changes is null ? null : JsonSerializer.Serialize(changes, Json);

    /// <summary>The column read back, or null when it holds nothing readable.</summary>
    public static IReadOnlyList<RunChangeDto>? Read(string? text)
    {
        if (text is null)
            return null;
        try
        {
            if (JsonNode.Parse(text) is not JsonArray lines)
                return null;
            var changes = new List<RunChangeDto>();
            foreach (var line in lines)
            {
                if (changes.Count == MaxLines)
                    break;
                if (line is not JsonObject item || Text(item["name"]) is not { } name)
                    continue;
                changes.Add(new RunChangeDto(Cap(name), Text(item["before"]) is { } before ? Cap(before) : null,
                    Text(item["after"]) is { } after ? Cap(after) : null));
            }

            return changes;
        }
        catch (Exception)
        {
            return null;
        }

        static string? Text(JsonNode? node) =>
            node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    }

    /// <summary>
    /// What the run changes, in step order and then its images; null when nothing was compared. A step whose outputs cannot
    /// be read adds nothing.
    /// </summary>
    public static List<RunChangeDto>? Of(RunContext? context, IEnumerable<Step> steps)
    {
        List<RunChangeDto>? changes = null;
        foreach (var step in steps.OrderBy(s => s.Ordinal))
        {
            try
            {
                var lines = step.Name switch
                {
                    StepNames.Preview => PreviewLines(step, context?.PreviousPrepared),
                    StepNames.Plan => Views.Plan(step) is { } plan ? PlanLines(plan) : null,
                    _ => null,
                };
                if (lines is not null)
                    (changes ??= []).AddRange(lines);
            }
            catch (Exception)
            {
                // Outputs an agent sent that do not read as they should say nothing about what changes.
            }
        }

        if (context?.ComposeImages is { } images)
        {
            try
            {
                (changes ??= []).AddRange(ImageLines(images.Images));
            }
            catch (Exception)
            {
                changes ??= [];
            }
        }

        return changes?.Take(MaxLines).Select(c => new RunChangeDto(Cap(c.Name), c.Before is null ? null : Cap(c.Before), c.After is null ? null : Cap(c.After))).ToList();
    }

    private static List<RunChangeDto>? PreviewLines(Step step, JsonNode? previous)
    {
        if (step.Outputs is null || JsonNode.Parse(step.Outputs) is not JsonObject outputs)
            return null;
        return PreparedChanges(outputs["prepared"], previous);
    }

    internal static string Cap(string text)
    {
        if (text.Length <= MaxText)
            return text;
        var cut = MaxText - 1;
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut] + "…";
    }

    // ----- Prepared values: a port of frontend/src/lib/prepared.ts. contracts/examples/run-changes.json holds the cases both agree on.

    private sealed record Leaf(string Text, string Json);

    private sealed class PreparedLeaf
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public required string Mark { get; init; }
        public string? Before { get; init; }
        public string? After { get; init; }
    }

    private sealed class Item
    {
        public required string Name { get; set; }
        public required string Path { get; init; }
        public required string Mark { get; init; }
        public string? Before { get; init; }
        public string? After { get; init; }
    }

    private const string Changed = "~", Added = "+", Removed = "−", Same = "";
    private static readonly string[] MarkOrder = [Changed, Added, Removed, Same];

    /// <summary>
    /// What a playbook run changes: the prepared items that differ from <paramref name="previous"/>, by name. Empty when it
    /// prepared the same values, null when there was nothing to compare: nothing prepared, or a baseline that is no object.
    /// </summary>
    public static List<RunChangeDto>? PreparedChanges(JsonNode? prepared, JsonNode? previous)
    {
        if (prepared is null || previous is not JsonObject)
            return null;
        var now = Flatten(prepared);
        var old = Flatten(previous);
        var groups = new OrderedDictionary<string, List<PreparedLeaf>>(StringComparer.Ordinal);
        foreach (var path in now.Keys.Concat(old.Keys.Where(p => !now.ContainsKey(p))))
        {
            var before = old.GetValueOrDefault(path);
            var after = now.GetValueOrDefault(path);
            var mark = before is null ? Added : after is null ? Removed : before.Json != after.Json ? Changed : Same;
            var dot = path.LastIndexOf('.');
            var parent = dot < 0 ? "" : path[..dot];
            var name = path[(dot + 1)..];
            var leaf = new PreparedLeaf { Name = name.Length > 0 ? name : "(value)", Path = path, Mark = mark, Before = before?.Text, After = after?.Text };
            if (!groups.TryGetValue(parent, out var leaves))
                groups.Add(parent, leaves = []);
            leaves.Add(leaf);
        }

        var items = new List<Item>();
        var things = new List<Item>();
        foreach (var (parent, leaves) in groups)
        {
            if (parent.Length > 0 && Thing(parent, leaves) is { } one)
            {
                items.Add(one);
                things.Add(one);
            }
            else
            {
                // A plain value keeps its whole path: `a.port` and `b.port` are not the same line.
                items.AddRange(leaves.Select(l => new Item { Name = l.Path.Length > 0 ? l.Path : "(value)", Path = l.Path, Mark = l.Mark, Before = l.Before, After = l.After }));
            }
        }

        ShortenNames(things);
        // Stable, as Array.prototype.sort is.
        return items.OrderBy(i => Array.IndexOf(MarkOrder, i.Mark))
            .Where(i => i.Mark != Same)
            .Select(i => new RunChangeDto(i.Name, i.Before, i.After))
            .ToList();
    }

    /// <summary>Leaf paths of prepared JSON. A list of plain values stays one leaf, so a new item is not a shifted index.</summary>
    private static OrderedDictionary<string, Leaf> Flatten(JsonNode? value)
    {
        var leaves = new OrderedDictionary<string, Leaf>(StringComparer.Ordinal);
        void Walk(JsonNode? node, string at, int depth)
        {
            // JsonNode.Parse refuses anything deeper than its own limit; this keeps a built node from going further.
            if (depth > 64)
                throw new InvalidOperationException("Too deep.");
            switch (node)
            {
                case JsonArray array when array.All(i => i is not JsonObject and not JsonArray):
                    leaves[at] = new Leaf(string.Join(", ", array.Select(LeafText)), Stringify(array));
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                        Walk(array[i], $"{at}[{i}]", depth + 1);
                    break;
                case JsonObject obj when obj.Count > 0:
                    foreach (var (key, child) in Entries(obj))
                        Walk(child, at.Length > 0 ? $"{at}.{key}" : key, depth + 1);
                    break;
                default:
                    leaves[at] = new Leaf(node is JsonObject ? "{}" : LeafText(node), Stringify(node));
                    break;
            }
        }

        Walk(value, "", 0);
        return leaves;
    }

    /// <summary>A plain value as text: a string as it is, anything else as JavaScript writes it.</summary>
    private static string LeafText(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : Stringify(node);

    /// <summary>
    /// An object's members in the order JavaScript's <c>Object.entries</c> gives them after <c>JSON.parse</c>: keys that
    /// are array indexes first, ascending, then the rest as written.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, JsonNode?>> Entries(JsonObject obj)
    {
        var indexes = obj.Where(p => ArrayIndex(p.Key) is not null).OrderBy(p => ArrayIndex(p.Key));
        return indexes.Concat(obj.Where(p => ArrayIndex(p.Key) is null));
    }

    private static uint? ArrayIndex(string key) =>
        key.Length is > 0 and <= 10 && (key == "0" || key[0] != '0') && key.All(char.IsAsciiDigit)
        && uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n < uint.MaxValue
            ? n
            : null;

    /// <summary>JSON as JavaScript's <c>JSON.stringify</c> writes what <c>JSON.parse</c> read: numbers as JavaScript prints them.</summary>
    internal static string Stringify(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonArray array:
                return "[" + string.Join(",", array.Select(Stringify)) + "]";
            case JsonObject obj:
                return "{" + string.Join(",", Entries(obj).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Stringify(p.Value))) + "}";
            case JsonValue value:
                return value.GetValueKind() switch
                {
                    JsonValueKind.String => JsonSerializer.Serialize(value.GetValue<string>()),
                    JsonValueKind.Number => NumberText(value),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => "null",
                };
            default:
                return "null";
        }
    }

    /// <summary>
    /// A JSON number as a double, read from its text as <c>JSON.parse</c> would, whatever type the node was made from; null
    /// for one that is no finite double.
    /// </summary>
    private static double? Finite(JsonValue value) =>
        double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;

    /// <summary>A JSON number as JavaScript's <c>String</c> prints it: <c>80</c>, <c>1.5</c>, <c>1e+21</c>.</summary>
    private static string NumberText(JsonValue value)
    {
        // A number too large for a double is Infinity to JavaScript, which JSON.stringify writes as null.
        if (Finite(value) is not { } number)
            return "null";
        if (number == 0)
            return "0";
        var abs = Math.Abs(number);
        if (abs >= 1e-6 && abs < 1e21)
        {
            var text = number.ToString("R", CultureInfo.InvariantCulture);
            if (!text.Contains('E'))
                return text;
            return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)
                ? exact.ToString(CultureInfo.InvariantCulture)
                : text;
        }

        var exponent = number.ToString("R", CultureInfo.InvariantCulture);
        var e = exponent.IndexOf('E');
        if (e < 0)
            return exponent;
        var mantissa = exponent[..e];
        var power = int.Parse(exponent[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        return $"{mantissa}e{(power < 0 ? "-" : "+")}{Math.Abs(power)}";
    }

    [GeneratedRegex(@"^([sS][hH][aA][0-9]+:)?[0-9a-fA-F]{32,}\z")]
    private static partial Regex Hash();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*://\S+\z")]
    private static partial Regex Url();

    private static bool IsHash(string? text) => text is not null && Hash().IsMatch(text);

    private static bool IsUrl(string? text) => text is not null && Url().IsMatch(text);

    /// <summary>The part of an address that tells a release: the first path segment that differs, else the file name.</summary>
    private static (string? Before, string? After) Telling(string? before, string? after)
    {
        static string? Last(string? url) => url?.Split('/').LastOrDefault(s => s.Length > 0);
        if (before is null || after is null || before == after)
            return (Last(before), Last(after));
        var x = before.Split('/');
        var y = after.Split('/');
        var at = -1;
        for (var i = 0; i < y.Length; i++)
        {
            if (i >= x.Length || y[i] != x[i])
            {
                at = i;
                break;
            }
        }

        return at < 0 || at >= x.Length ? (Last(before), Last(after)) : (x[at], y[at]);
    }

    private static string GroupMark(List<PreparedLeaf> leaves)
    {
        if (leaves.All(l => l.Mark == Added))
            return Added;
        if (leaves.All(l => l.Mark == Removed))
            return Removed;
        return leaves.Any(l => l.Mark != Same) ? Changed : Same;
    }

    /// <summary>
    /// A group of values is one thing when one of them tells the rest: a <c>version</c> or <c>tag</c>, or an address next
    /// to its checksum. Anything else stays one line per value, so nothing is hidden behind a guess.
    /// </summary>
    private static Item? Thing(string path, List<PreparedLeaf> leaves)
    {
        var version = leaves.FirstOrDefault(l => l.Name is "version" or "tag");
        var url = leaves.FirstOrDefault(l => IsUrl(l.After ?? l.Before));
        var checksum = leaves.Any(l => IsHash(l.After ?? l.Before));
        (string? Before, string? After) head;
        if (version is not null)
            head = (version.Before, version.After);
        else if (url is not null && checksum)
            head = Telling(url.Before, url.After);
        else
            return null;
        return new Item { Name = path, Path = path, Mark = GroupMark(leaves), Before = head.Before, After = head.After };
    }

    /// <summary><c>machine_resolved_t3</c> and <c>machine_resolved_codex</c> are <c>t3</c> and <c>codex</c>: what they share says nothing.</summary>
    private static void ShortenNames(List<Item> things)
    {
        if (things.Count < 2)
            return;
        var shared = things[0].Path;
        foreach (var item in things)
        {
            var i = 0;
            while (i < shared.Length && i < item.Path.Length && shared[i] == item.Path[i])
                i++;
            shared = shared[..i];
        }

        var cut = Math.Max(shared.LastIndexOf('_'), Math.Max(shared.LastIndexOf('-'), shared.LastIndexOf('.'))) + 1;
        if (cut == 0 || things.Any(t => t.Path.Length == cut))
            return;
        foreach (var item in things)
            item.Name = item.Path[cut..];
    }

    // ----- Compose images: a port of imageChanges in frontend/src/lib/images.ts.

    private sealed record ImageRef(string Repository, string? Tag, string? Digest);

    /// <summary>One line per service whose image reference changed, with the version as shown: the tag, else a short digest.</summary>
    public static IEnumerable<RunChangeDto> ImageLines(IEnumerable<ComposeImageChange>? images)
    {
        foreach (var image in images ?? [])
        {
            if (image is null || image.Before == image.After)
                continue;
            var old = image.Before is null ? null : ParseImage(image.Before);
            var next = image.After is null ? null : ParseImage(image.After);
            // Digests only when the tags alone would read the same.
            var sameTag = old is not null && next is not null && old.Tag == next.Tag;
            yield return new RunChangeDto(image.Service ?? "?", old is null ? null : VersionText(old, sameTag), next is null ? null : VersionText(next, sameTag));
        }
    }

    /// <summary>"registry:5000/team/app:1.2@sha256:…": the tag is after the last colon that follows the last slash.</summary>
    private static ImageRef ParseImage(string reference)
    {
        var at = reference.IndexOf('@');
        var digest = at >= 0 && reference.Length > at + 1 ? reference[(at + 1)..] : null;
        var name = at >= 0 ? reference[..at] : reference;
        var colon = name.LastIndexOf(':');
        if (colon > name.LastIndexOf('/'))
        {
            var tag = name[(colon + 1)..];
            return new ImageRef(name[..colon], tag.Length > 0 ? tag : null, digest);
        }

        return new ImageRef(name, null, digest);
    }

    /// <summary>"sha256:0123456789ab", from a full digest.</summary>
    private static string ShortDigest(string digest)
    {
        var parts = digest.Split(':');
        var (algorithm, hex) = parts.Length > 1 ? (parts[0], parts[1]) : ("", digest);
        var hexShort = hex.Length > 12 ? hex[..12] : hex;
        return algorithm.Length > 0 ? $"{algorithm}:{hexShort}" : hexShort;
    }

    private static string VersionText(ImageRef image, bool withDigest)
    {
        if (image.Tag is not null && image.Digest is not null && withDigest)
            return $"{image.Tag}@{ShortDigest(image.Digest)}";
        if (image.Tag is not null)
            return image.Tag;
        if (image.Digest is not null)
            return ShortDigest(image.Digest);
        return "no tag";
    }

    // ----- Plans: the allowlisted VM settings a plan changes, then the resources that changed otherwise.

    /// <summary>
    /// One line per VM setting whose value is known, not sensitive and differs; then one line counting the resources that
    /// change without such a line, by kind.
    /// </summary>
    public static List<RunChangeDto> PlanLines(PlanDto plan)
    {
        var lines = new List<RunChangeDto>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var change in plan.Changes)
        {
            var before = lines.Count;
            foreach (var attribute in change.Attributes)
            {
                if (attribute.Sensitive || attribute.Unknown || Stringify(attribute.Before) == Stringify(attribute.After))
                    continue;
                if (AttributeLine(attribute) is { } line)
                    lines.Add(line);
            }

            var kind = Kind(change.Actions);
            if (lines.Count == before && kind is not (ChangeKinds.NoOp or ChangeKinds.Read))
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }

        if (counts.Count > 0)
        {
            var words = ChangeKinds.Counted.Where(counts.ContainsKey)
                .Select(k => k == ChangeKinds.Other ? $"{counts[k]} other" : $"{counts[k]} to {k}");
            lines.Add(new RunChangeDto("resources", null, string.Join(", ", words)));
        }

        return lines;
    }

    private static class ChangeKinds
    {
        public const string Create = "create", Update = "update", Replace = "replace", Delete = "delete", Read = "read", NoOp = "no-op", Other = "other";
        public static readonly string[] Counted = [Create, Update, Replace, Delete, Other];
    }

    /// <summary>OpenTofu's action list as a kind: a replace is a delete and a create, in either order.</summary>
    private static string Kind(IReadOnlyList<string> actions) => string.Join(",", actions) switch
    {
        "create" => ChangeKinds.Create,
        "update" => ChangeKinds.Update,
        "delete" => ChangeKinds.Delete,
        "delete,create" or "create,delete" => ChangeKinds.Replace,
        "read" => ChangeKinds.Read,
        "no-op" or "" => ChangeKinds.NoOp,
        _ => ChangeKinds.Other,
    };

    private static RunChangeDto? AttributeLine(PlanAttributeDto attribute)
    {
        var name = attribute.Name;
        var parts = name.Split('.');
        if (parts is ["disk", var device, "size"])
            return new RunChangeDto($"disk {device}", WithUnit(Scalar(attribute.Before), "GiB"), WithUnit(Scalar(attribute.After), "GiB"));
        if (name == "memory.dedicated")
        {
            static double? Number(JsonNode? node) =>
                IsNumber(node) ? Finite(node!.AsValue()) : null;
            var sides = new[] { attribute.Before, attribute.After }.Select(Number).OfType<double>().ToList();
            var gib = sides.Count > 0 && sides.All(v => v % 1024 == 0);
            string? Memory(JsonNode? side) => gib && Number(side) is { } mib
                ? WithUnit(NumberText(JsonValue.Create(mib / 1024)), "GiB")
                : WithUnit(Scalar(side), gib ? "GiB" : "MiB");
            return new RunChangeDto("memory", Memory(attribute.Before), Memory(attribute.After));
        }

        if (parts is ["cpu", var cpu])
            return new RunChangeDto($"cpu {cpu}", Scalar(attribute.Before), Scalar(attribute.After));
        if (parts is ["network_device", var index, "bridge"])
            return new RunChangeDto($"net{index} bridge", Scalar(attribute.Before), Scalar(attribute.After));
        if (name == "tags")
            return new RunChangeDto("tags", Tags(attribute.Before), Tags(attribute.After));
        return null;
    }

    private static bool IsNumber(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number;

    /// <summary>A plain value as text; null for none, or for anything that is not a string, number or boolean.</summary>
    private static string? Scalar(JsonNode? node) => node is JsonValue value
        ? value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => Finite(value) is null ? null : NumberText(value),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        }
        : null;

    private static string? WithUnit(string? value, string unit) => value is null ? null : $"{value} {unit}";

    private static string? Tags(JsonNode? node)
    {
        var tags = (node as JsonArray)?.OfType<JsonValue>().Where(t => t.GetValueKind() == JsonValueKind.String).Select(t => t.GetValue<string>()).ToList();
        return tags is { Count: > 0 } ? string.Join(", ", tags) : null;
    }
}
