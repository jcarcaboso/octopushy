using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Octopushy.Definitions.Schemas;

/// <summary>One error found by a JSON Schema, at a JSON pointer into the instance.</summary>
public sealed record SchemaError(string Pointer, string Message);

/// <summary>The definition kinds, each validated by one schema from <c>contracts/schemas</c>.</summary>
public enum DefinitionKind
{
    Repository,
    Lab,
    Installation,
    Guest,
    Template,
    Recipe,
}

/// <summary>The JSON Schemas shared with the editor, embedded from <c>contracts/schemas</c>.</summary>
public sealed class SchemaCatalog
{
    private const string BaseUri = "https://schemas.octopushy.invalid/v1/";

    private static readonly Lazy<SchemaCatalog> Shared = new(() => new SchemaCatalog());

    private readonly Dictionary<DefinitionKind, JsonSchema> _schemas = [];

    private SchemaCatalog()
    {
        var options = NewBuildOptions();
        Build(options, "common");
        _schemas[DefinitionKind.Repository] = Build(options, "octopushy");
        _schemas[DefinitionKind.Lab] = Build(options, "lab");
        _schemas[DefinitionKind.Installation] = Build(options, "installation");
        _schemas[DefinitionKind.Guest] = Build(options, "guest");
        _schemas[DefinitionKind.Template] = Build(options, "template");
        _schemas[DefinitionKind.Recipe] = Build(options, "recipe");
    }

    public static SchemaCatalog Instance => Shared.Value;

    /// <summary>The schema text for a kind, as served to the editor.</summary>
    public static string SchemaText(string name)
    {
        using var stream = typeof(SchemaCatalog).Assembly.GetManifestResourceStream($"schemas/{name}.schema.json")
            ?? throw new InvalidOperationException($"The schema '{name}' is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public IReadOnlyList<SchemaError> Validate(DefinitionKind kind, JsonNode? instance) =>
        Evaluate(_schemas[kind], instance);

    /// <summary>
    /// Builds a schema supplied by a lab repository, such as a recipe's inputs schema. It may
    /// not reference anything outside itself: nothing is fetched.
    /// </summary>
    public static JsonSchema BuildUntrusted(string text) =>
        JsonSchema.FromText(text, NewBuildOptions(), new Uri("urn:octopushy:repository-schema"));

    public static IReadOnlyList<SchemaError> Evaluate(JsonSchema schema, JsonNode? instance)
    {
        using var document = JsonDocument.Parse(instance?.ToJsonString() ?? "null");
        var results = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            return [];
        }

        var errors = new List<SchemaError>();
        var seen = new HashSet<(string, string)>();
        foreach (var detail in (results.Details ?? []).Prepend(results))
        {
            if (detail.IsValid || detail.Errors is null)
            {
                continue;
            }

            var pointer = detail.InstanceLocation.ToString();
            foreach (var (keyword, message) in detail.Errors)
            {
                // Applicators only summarize the errors of their subschemas, which are reported themselves.
                if (Summaries.Contains(keyword))
                {
                    continue;
                }

                if (seen.Add((pointer, message)))
                {
                    errors.Add(new SchemaError(pointer, message));
                }
            }
        }

        if (errors.Count == 0)
        {
            errors.Add(new SchemaError("", "The document does not match its schema."));
        }

        return errors;
    }

    private static readonly HashSet<string> Summaries =
    [
        "properties", "patternProperties", "additionalProperties", "items", "prefixItems", "contains",
        "allOf", "$ref", "$dynamicRef", "dependentSchemas", "then", "else", "propertyNames", "unevaluatedProperties", "unevaluatedItems",
    ];

    private static BuildOptions NewBuildOptions() => new()
    {
        SchemaRegistry = new SchemaRegistry { Fetch = static (_, _) => null },
    };

    private static JsonSchema Build(BuildOptions options, string name) =>
        JsonSchema.FromText(SchemaText(name), options, new Uri(BaseUri + name + ".schema.json"));
}
