using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Octopushy.Definitions;

/// <summary>
/// Everything a guest's provisioning run is built from, and a hash of it that changes exactly
/// when a new plan or reporter setup could differ: the guest's definition, its host and
/// installation settings, its template, its recipe and root files, its external state
/// backend, and its disk reporter overrides.
/// </summary>
public sealed record ProvisionInputs(
    GuestDefinition Guest,
    InstallationDefinition Installation,
    HostDefinition Host,
    RecipeDefinition Recipe,
    TemplateDefinition? Template,
    StateBackendDefinition? Backend,
    JsonObject Variables,
    string Hash,
    ReviewedCode Code)
{
    /// <summary>Guest fields excluded from the infrastructure hash; reporter setup is hashed separately.</summary>
    internal static readonly string[] NotProvisioned = ["description", "policy", "execution", "setup", "renamedFrom"];

    /// <summary>Builds the inputs for a valid guest; the repository must have validated it.</summary>
    public static ProvisionInputs Build(LabRepository repository, GuestDefinition guest, string revision)
    {
        var installation = repository.Installation(guest.InstallationAddress)
            ?? throw new InvalidOperationException($"{guest.Address} has no installation.");
        var host = installation.Hosts[guest.Host];
        var recipe = repository.Recipes[guest.Recipe];
        var template = guest.Template is null ? null : repository.Templates[guest.Template];
        var backend = guest.StateBackend is { } name ? repository.Settings!.StateBackends[name] : null;

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
        if (guest.Json["provision"]?["inputs"] is { } recipeInputs)
            variables["guest"]!["inputs"] = recipeInputs.DeepClone();

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
        };
        if (backend is not null)
        {
            hashed["backend"] = backend.Json.DeepClone();
        }
        // Omitted without modules, like diskReporter below, to preserve existing hashes.
        if (recipe.Modules is { Count: > 0 } modules)
        {
            hashed["moduleFiles"] = new JsonArray(modules.Select(m => (JsonNode?)TreeHash(repository.Root, m)).ToArray());
        }
        // Reporter setup changes need a new run, but never become OpenTofu inputs.
        // Omit the key entirely for existing definitions to preserve their hashes.
        if (guest.Json["setup"]?["diskReporter"] is { } diskReporter)
        {
            hashed["diskReporter"] = diskReporter.DeepClone();
        }

        return new ProvisionInputs(guest, installation, host, recipe, template, backend, variables, Canonical.Hash(hashed),
            ReviewedCode.Of(repository, recipe));
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

/// <summary>
/// The code an operator reviews for a recipe: what <see cref="Digest"/> covers, and the only
/// folders a run holding a protected secret receives, so nothing outside it can execute.
/// </summary>
public sealed record ReviewedCode(string Digest, IReadOnlyList<string> Paths)
{
    public static ReviewedCode Of(LabRepository repository, RecipeDefinition recipe)
    {
        var folder = Path.GetDirectoryName(recipe.File)!.Replace('\\', '/');
        var modules = recipe.Modules ?? [];
        var hashed = new JsonObject
        {
            ["recipe"] = recipe.Json.DeepClone(),
            ["recipeFiles"] = ProvisionInputs.TreeHash(repository.Root, folder),
            ["rootFiles"] = ProvisionInputs.TreeHash(repository.Root, recipe.Root),
            ["moduleFiles"] = new JsonArray(modules.Select(m => (JsonNode?)ProvisionInputs.TreeHash(repository.Root, m)).ToArray()),
        };
        return new ReviewedCode(Canonical.Hash(hashed), [folder, recipe.Root, .. modules]);
    }
}

/// <summary>
/// What a grant of a protected secret covers for a guest: its reviewed code, and everything that
/// decides who ends up with the secret when that code does not change. A commit can leave every
/// line of a root alone and still change where its state goes, which variable a secret becomes,
/// which agent runs it, what container it is installed in, who can log in to that container, or
/// which credential reaches the Proxmox API that creates it. So this is the guest as it is
/// provisioned, whole, except what cannot affect it; and the host and the installation's
/// credentials it is created through. <see cref="Execution"/> is that, as an operator is shown it
/// before trusting.
/// </summary>
public sealed record ReleaseBinding(string Digest, JsonObject Execution)
{
    public static ReleaseBinding Of(LabRepository repository, GuestDefinition guest)
    {
        var code = repository.Recipes.TryGetValue(guest.Recipe, out var recipe) ? ReviewedCode.Of(repository, recipe).Digest : "";
        var installation = repository.Installation(guest.InstallationAddress);
        var host = installation is not null && installation.Hosts.TryGetValue(guest.Host, out var found) ? found : null;
        var endpoint = installation is not null && host is not null ? ProvisionInputs.EndpointFor(installation, host) : null;
        var backend = guest.StateBackend is { } name && repository.Settings?.StateBackends.TryGetValue(name, out var declared) == true ? declared : null;
        var template = guest.Template is { } id && repository.Templates.TryGetValue(id, out var image) ? image : null;
        var provisioned = guest.Json.DeepClone().AsObject();
        foreach (var field in ProvisionInputs.NotProvisioned)
            provisioned.Remove(field);
        var execution = new JsonObject
        {
            ["backend"] = backend?.Json.DeepClone() ?? new JsonObject { ["type"] = "built-in" },
            ["secrets"] = new JsonObject(guest.ProvisionSecrets.OrderBy(s => s.Key, StringComparer.Ordinal)
                .Select(s => KeyValuePair.Create(s.Key, (JsonNode?)s.Value.Key))),
            ["agentLabels"] = new JsonArray(PolicyResolver.AgentLabels(repository, guest).Value.Select(l => (JsonNode?)l).ToArray()),
            ["proxmox"] = new JsonObject
            {
                ["endpoint"] = endpoint,
                ["verifyTls"] = installation?.Connection.VerifyTls,
                // Which store entries are sent to the API, as the token and as its CA.
                ["readOnlyToken"] = Reference(installation?.Connection.ReadOnlyToken),
                ["privilegedToken"] = Reference(installation?.Connection.PrivilegedToken),
                ["sharedToken"] = installation?.Connection.SharedToken,
                ["caCertificate"] = Reference(installation?.Connection.CaCertificate),
            },
            ["template"] = template?.Json.DeepClone(),
            ["inputs"] = guest.Json["provision"]?["inputs"]?.DeepClone(),
            ["network"] = guest.Json["network"]?.DeepClone(),
            // Who can log in (access.sshAuthorizedKeys), where it is, what it mounts, what it is.
            ["guest"] = provisioned,
            ["host"] = host?.Json.DeepClone(),
        };
        return new ReleaseBinding(Canonical.Hash(new JsonObject { ["code"] = code, ["execution"] = execution.DeepClone() }), execution);
    }

    private static JsonNode? Reference(StoreReference? reference) =>
        reference is null ? null : new JsonObject { ["store"] = reference.Key, ["version"] = reference.Version };
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
