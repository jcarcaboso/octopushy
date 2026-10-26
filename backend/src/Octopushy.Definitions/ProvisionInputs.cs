using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Octopushy.Definitions;

/// <summary>
/// Everything a guest's provisioning run is built from, and a hash of it that changes exactly
/// when a new plan or reporter setup could differ: the guest's definition, its host and
/// installation settings, its template, its recipe and root files, its state backend,
/// and its disk reporter overrides.
/// </summary>
public sealed record ProvisionInputs(
    GuestDefinition Guest,
    InstallationDefinition Installation,
    HostDefinition Host,
    RecipeDefinition Recipe,
    TemplateDefinition? Template,
    StateBackendDefinition Backend,
    JsonObject Variables,
    string Hash)
{
    /// <summary>Guest fields excluded from the infrastructure hash; reporter setup is hashed separately.</summary>
    private static readonly string[] NotProvisioned = ["description", "policy", "execution", "setup", "renamedFrom"];

    /// <summary>Builds the inputs for a valid guest; the repository must have validated it.</summary>
    public static ProvisionInputs Build(LabRepository repository, GuestDefinition guest, string revision)
    {
        var installation = repository.Installation(guest.InstallationAddress)
            ?? throw new InvalidOperationException($"{guest.Address} has no installation.");
        var host = installation.Hosts[guest.Host];
        var recipe = repository.Recipes[guest.Recipe];
        var template = guest.Template is null ? null : repository.Templates[guest.Template];
        var backend = repository.Settings!.StateBackends[guest.StateBackend];

        var variables = new JsonObject
        {
            ["octopushy"] = new JsonObject
            {
                ["address"] = guest.Address,
                ["stack"] = guest.Stack,
                ["lab"] = guest.Lab,
                ["installation"] = guest.Installation,
                ["revision"] = revision,
            },
            ["guest"] = new JsonObject
            {
                ["id"] = guest.Id,
                ["vmid"] = guest.Vmid,
                ["host"] = guest.Host,
                ["template"] = guest.Template,
                ["profiles"] = guest.Json["profiles"]?.DeepClone() ?? new JsonArray(),
                ["hardware"] = guest.Json["hardware"]?.DeepClone() ?? new JsonObject(),
                ["network"] = guest.Json["network"]?.DeepClone() ?? new JsonObject(),
                ["sshAuthorizedKeys"] = guest.Json["access"]?["sshAuthorizedKeys"]?.DeepClone() ?? new JsonArray(),
            },
            ["host"] = new JsonObject
            {
                ["id"] = host.Id,
                ["datastores"] = host.Json["datastores"]?.DeepClone() ?? new JsonObject(),
                ["bridges"] = host.Json["bridges"]?.DeepClone() ?? new JsonArray(),
            },
            ["installation"] = new JsonObject
            {
                ["id"] = installation.Id,
                ["type"] = installation.Type,
                ["endpoint"] = EndpointFor(installation, host),
                ["verifyTls"] = installation.Connection.VerifyTls,
            },
            ["template"] = template?.Image is { } image
                ? new JsonObject
                {
                    ["id"] = template.Id,
                    ["os"] = template.Os,
                    ["source"] = new JsonObject
                    {
                        ["url"] = image.Url,
                        ["checksum"] = image.Checksum,
                        ["checksumAlgorithm"] = image.ChecksumAlgorithm,
                        ["fileName"] = image.FileName,
                    },
                }
                : null,
        };
        if (guest.ManagedWorker is { } worker)
            variables["guest"]!["workerGeneration"] = worker.Generation.ToString();

        var guestJson = guest.Json.DeepClone().AsObject();
        foreach (var field in NotProvisioned)
        {
            guestJson.Remove(field);
        }

        var hashed = new JsonObject
        {
            ["guest"] = guestJson,
            ["installation"] = new JsonObject
            {
                ["type"] = installation.Type,
                ["connection"] = installation.Json["connection"]?.DeepClone(),
            },
            ["host"] = host.Json.DeepClone(),
            ["template"] = template?.Json.DeepClone(),
            ["recipe"] = recipe.Json.DeepClone(),
            ["recipeFiles"] = TreeHash(repository.Root, Path.GetDirectoryName(recipe.File)!),
            ["rootFiles"] = TreeHash(repository.Root, recipe.Root),
            ["backend"] = backend.Json.DeepClone(),
        };
        // Reporter setup changes need a new run, but never become OpenTofu inputs.
        // Omit the key entirely for existing definitions to preserve their hashes.
        if (guest.Json["setup"]?["diskReporter"] is { } diskReporter)
        {
            hashed["diskReporter"] = diskReporter.DeepClone();
        }

        return new ProvisionInputs(guest, installation, host, recipe, template, backend, variables, Canonical.Hash(hashed));
    }

    /// <summary>The API endpoint for a host: its own when set, otherwise the installation's.</summary>
    public static string EndpointFor(InstallationDefinition installation, HostDefinition host) =>
        host.Endpoint ?? installation.Connection.Endpoint;

    /// <summary>The expected static or reserved DHCP address, without a prefix; null for plain DHCP.</summary>
    public static string? ExpectedIpv4(GuestDefinition guest)
    {
        var network = guest.Json["network"] as JsonObject;
        if (network?["mode"]?.GetValue<string>() is "dhcp")
        {
            return network["reservedAddress"]?.GetValue<string>();
        }

        return network?["address"] is JsonValue address && address.TryGetValue<string>(out var text)
            ? text.Split('/')[0]
            : null;
    }

    /// <summary>A hash of every file below a repository directory, by relative path and content.</summary>
    public static string TreeHash(string repositoryRoot, string relativeDirectory)
    {
        var directory = Path.Combine(repositoryRoot, relativeDirectory);
        if (!Directory.Exists(directory))
        {
            return "missing";
        }

        var builder = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Select(f => (Path: Path.GetRelativePath(directory, f).Replace('\\', '/'), Full: f))
                     .Where(f => !f.Path.Split('/').Any(part => part is ".terraform"))
                     .OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            using var stream = File.OpenRead(file.Full);
            builder.Append(file.Path).Append('\0').Append(Convert.ToHexStringLower(SHA256.HashData(stream))).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

/// <summary>Canonical JSON (keys sorted, no whitespace) and its SHA-256, for change detection.</summary>
public static class Canonical
{
    public static string Hash(JsonNode? node) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(node))));

    public static string Serialize(JsonNode? node) => Sort(node)?.ToJsonString() ?? "null";

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
        _ => node?.DeepClone(),
    };
}
