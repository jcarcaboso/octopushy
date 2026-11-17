using System.Text.Json;
using System.Text.Json.Nodes;

namespace Octopushy.Tests;

/// <summary>
/// The executor examples in <c>contracts/agent/v1/examples</c>: one input and one output per action, which the backend and the
/// agent are both tested against. The backend writes inputs and the agent writes outputs, so each side checks what it produces
/// against the file and the other side checks what it reads. See the README there.
/// </summary>
public static class ContractExamples
{
    /// <summary>Set to 1 to write what the code produces into the examples instead of comparing it with them.</summary>
    public const string WriteVariable = "OCTOPUSHY_WRITE_CONTRACT_EXAMPLES";

    public static string Directory => Path.Combine(TestRepository.SourceRoot, "contracts", "agent", "v1", "examples");

    private static string PathOf(string executor, string action, string kind, string? variant) =>
        Path.Combine(Directory, executor, $"{action}.{kind}{(variant is null ? "" : "." + variant)}.json");

    public static bool Exists(string executor, string action, string kind = "output", string? variant = null) =>
        File.Exists(PathOf(executor, action, kind, variant));

    public static JsonObject Input(string executor, string action) => Load(PathOf(executor, action, "input", null));

    public static JsonObject Output(string executor, string action, string? variant = null) => Load(PathOf(executor, action, "output", variant));

    private static JsonObject Load(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    /// <summary>
    /// Compares an input the backend built with its example, or writes it there in write mode. Keys must be the same at every
    /// level, arrays as long, and each value of the same JSON kind. Values are equal too, except at <paramref name="varying"/>:
    /// paths such as <c>context/run</c>, with <c>*</c> for any array index or object key, whose value depends on the run.
    /// </summary>
    public static void AssertInput(string executor, string action, string actualJson, params string[] varying)
    {
        var path = PathOf(executor, action, "input", null);
        var actual = JsonNode.Parse(actualJson)!;
        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + "\n");
            return;
        }

        var differences = Differences(Load(path), actual, varying);
        Assert.True(differences.Count == 0,
            $"{executor}/{action}: the input differs from {Path.GetRelativePath(TestRepository.SourceRoot, path)}:\n  " + string.Join("\n  ", differences));
    }

    /// <summary>Where <paramref name="actual"/> differs from <paramref name="expected"/>, as described at <see cref="AssertInput"/>.</summary>
    public static List<string> Differences(JsonNode? expected, JsonNode? actual, IReadOnlyCollection<string> varying)
    {
        var differences = new List<string>();
        Compare(expected, actual, "", varying, differences);
        return differences;
    }

    private static void Compare(JsonNode? expected, JsonNode? actual, string path, IReadOnlyCollection<string> varying, List<string> differences)
    {
        var where = path.Length == 0 ? "/" : path;
        if (Kind(expected) != Kind(actual))
        {
            differences.Add($"{where}: expected {Kind(expected)}, got {Kind(actual)}");
            return;
        }

        switch (expected)
        {
            case JsonObject e:
                var a = actual!.AsObject();
                foreach (var key in e.Select(p => p.Key).Except(a.Select(p => p.Key)))
                    differences.Add($"{path}/{key}: missing");
                foreach (var key in a.Select(p => p.Key).Except(e.Select(p => p.Key)))
                    differences.Add($"{path}/{key}: not in the example");
                foreach (var (key, value) in e.Where(p => a.ContainsKey(p.Key)))
                    Compare(value, a[key], $"{path}/{key}", varying, differences);
                break;
            case JsonArray e:
                var list = actual!.AsArray();
                if (e.Count != list.Count)
                    differences.Add($"{where}: expected {e.Count} items, got {list.Count}");
                for (var i = 0; i < Math.Min(e.Count, list.Count); i++)
                    Compare(e[i], list[i], $"{path}/{i}", varying, differences);
                break;
            default:
                if (!Varies(path, varying) && !JsonNode.DeepEquals(expected, actual))
                    differences.Add($"{where}: expected {expected?.ToJsonString() ?? "null"}, got {actual?.ToJsonString() ?? "null"}");
                break;
        }
    }

    private static bool Varies(string path, IReadOnlyCollection<string> varying)
    {
        var parts = path.TrimStart('/').Split('/');
        return varying.Any(pattern =>
        {
            var wanted = pattern.Split('/');
            return wanted.Length == parts.Length && wanted.Zip(parts).All(p => p.First == "*" || p.First == p.Second);
        });
    }

    /// <summary>The JSON kind of a value, with true and false as one.</summary>
    public static string Kind(JsonNode? node) => node?.GetValueKind() switch
    {
        null or JsonValueKind.Null => "null",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Number => "number",
        JsonValueKind.String => "string",
        JsonValueKind.Array => "array",
        _ => "object",
    };

    /// <summary>
    /// What in <paramref name="outputs"/>, which a test made up, has a shape the example's outputs do not: a key the example
    /// does not have, or a value of another kind where the example's is not null. A key may be left out, and a value changed.
    /// Inside <paramref name="free"/> paths, whose keys are the user's (such as an apply's module outputs), nothing is checked.
    /// An array's items are checked against the example's items together: a key any of them has is allowed.
    /// </summary>
    public static List<string> ShapeProblems(JsonNode example, JsonNode? outputs, IReadOnlyCollection<string> free)
    {
        var problems = new List<string>();
        Shape(example, outputs, "", free, problems);
        return problems;
    }

    private static void Shape(JsonNode? example, JsonNode? given, string path, IReadOnlyCollection<string> free, List<string> problems)
    {
        if (given is null || example is null || Varies(path, free))
            return;
        if (Kind(example) != Kind(given))
        {
            problems.Add($"{(path.Length == 0 ? "/" : path)}: {Kind(given)} where the example has {Kind(example)}");
            return;
        }

        switch (example)
        {
            case JsonObject e:
                foreach (var (key, value) in given.AsObject())
                {
                    if (Varies($"{path}/{key}", free))
                        continue;
                    if (!e.TryGetPropertyValue(key, out var expected))
                        problems.Add($"{path}/{key}: not in the example");
                    else
                        Shape(expected, value, $"{path}/{key}", free, problems);
                }

                break;
            case JsonArray e when e.Count > 0:
                // Items are checked against all the example's items merged, each key keeping the first non-null value.
                var merged = e.Skip(1).Aggregate(e[0]?.DeepClone(), Merge);
                for (var i = 0; i < given.AsArray().Count; i++)
                    Shape(merged, given[i], $"{path}/{i}", free, problems);
                break;
        }
    }

    private static JsonNode? Merge(JsonNode? into, JsonNode? item)
    {
        if (into is JsonArray list && item is JsonArray more)
        {
            foreach (var value in more)
                list.Add(value?.DeepClone());
            return list;
        }

        if (into is JsonObject target && item is JsonObject source)
        {
            foreach (var (key, value) in source)
            {
                if (!target.TryGetPropertyValue(key, out var existing) || existing is null)
                    target[key] = value?.DeepClone();
                else
                    target[key] = Merge(existing, value);
            }

            return target;
        }

        return into ?? item?.DeepClone();
    }
}
