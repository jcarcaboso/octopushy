using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Octopushy.Definitions.Schemas;
using Octopushy.Definitions.Yaml;

namespace Octopushy.Definitions;

/// <summary>What reading the repository's definitions and the workflows they name have in common.</summary>
internal static partial class DefinitionFiles
{
    public static bool IsId(string id) => IdPattern().IsMatch(id);

    public static bool DeclaresBackend(string tofuText) => BackendBlock().IsMatch(tofuText);

    /// <summary>A JSON pointer as an error message leads with it, such as <c>hardware.cores: </c>.</summary>
    public static string Describe(string pointer) => pointer.Length == 0 ? "" : $"{pointer.TrimStart('/').Replace('/', '.')}: ";

    public static IEnumerable<string> SubDirectories(string path) =>
        Directory.Exists(path)
            ? Directory.EnumerateDirectories(path)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .Order(StringComparer.Ordinal)
            : [];

    /// <summary>
    /// Reads a definition file and checks it against its schema. Errors go to <paramref name="error"/>, with the file's line;
    /// the result is null unless the file is valid YAML that matches its schema.
    /// </summary>
    public static (JsonObject? Json, YamlDocument? Document) Read(
        string root, string file, DefinitionKind kind, Dictionary<string, YamlDocument> documents, Action<string, int?, string> error)
    {
        YamlDocument document;
        try
        {
            document = YamlDocumentReader.Read(File.ReadAllText(Path.Combine(root, file)));
        }
        catch (YamlReadException e)
        {
            error(file, e.Line, $"Invalid YAML: {e.Message}");
            return (null, null);
        }

        documents[file] = document;
        if (document.Root is not JsonObject json)
        {
            error(file, 1, "The file must hold a YAML mapping.");
            return (null, null);
        }

        var errors = SchemaCatalog.Instance.Validate(kind, json);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schemaError in errors)
        {
            var message = Explain(json, schemaError);
            if (reported.Add(message))
                error(file, document.LineOf(schemaError.Pointer), message);
        }

        return errors.Count == 0 ? (json, document) : (null, null);
    }

    /// <summary>A schema's message, or a plainer one for the mistakes that are easy to make in these files.</summary>
    private static string Explain(JsonObject json, SchemaError error)
    {
        if (error.Pointer == "/workflow" && json["workflow"] is JsonValue reference && reference.TryGetValue<string>(out var text) && !text.Contains('@'))
            return $"workflow: '{text}' names no version. Write {text}@<major> for its newest <major>.x, or {text}@<major>.<minor> for exactly one.";
        if (error.Pointer == "/version" && json["version"] is JsonValue version && !version.TryGetValue<string>(out _))
            return $"version: quote it, as in version: \"{version.ToJsonString()}\". Unquoted, YAML reads it as a number, and 2.10 would be 2.1.";
        // How a closed schema refuses a field it does not have.
        if (error.Message == "All values fail against the false schema" && error.Pointer.Length > 0)
            return Describe(error.Pointer) + "not allowed here.";
        return Describe(error.Pointer) + error.Message;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\s*backend\s+""", RegexOptions.Multiline)]
    private static partial Regex BackendBlock();
}
