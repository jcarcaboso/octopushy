using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Octopushy.Definitions;

/// <summary>
/// Everything a guest's provisioning run is built from, and a hash of it that changes exactly
/// when a new plan could differ: the guest's definition, its host and installation settings,
/// its template, and the workflow it resolved to: its version, its folder and the root of the
/// stage that makes the machine.
/// </summary>
public sealed record ProvisionInputs(
    GuestDefinition Guest,
    InstallationDefinition Installation,
    HostDefinition Host,
    WorkflowDefinition Workflow,
    WorkflowStage Stage,
    TemplateDefinition? Template,
    JsonObject Variables,
    string Hash)
{
    /// <summary>For a built template, the image the variables name, or null when it was not given: see <see cref="Build"/>.</summary>
    public TemplateImage? Image { get; init; }

    /// <summary>Guest fields excluded from the infrastructure hash.</summary>
    private static readonly string[] NotProvisioned =
        ["description", "policy", "execution", "secrets", "secretFiles", "schedules", "renamedFrom", "workflow"];

    /// <summary>Builds the inputs for a valid guest; the repository must have validated it.</summary>
    /// <param name="keyIssued">Whether Octopushy already holds a key for the guest; see <see cref="ManagedAccess.Wanted"/>.</param>
    /// <param name="hostKeyIssued">Whether Octopushy already holds a host key for the guest; see <see cref="ManagedHostKey.Wanted"/>.</param>
    /// <param name="image">
    /// For a guest whose template is built, the image built for the template's key. Without it the variables name no file, and
    /// only the hash is of use: a provision run waits for the image. A downloaded template ignores it.
    /// </param>
    public static ProvisionInputs Build(LabRepository repository, GuestDefinition guest, string revision, bool keyIssued = false,
        bool hostKeyIssued = false, TemplateImage? image = null)
    {
        var installation = repository.Installation(guest.InstallationAddress)
            ?? throw new InvalidOperationException($"{guest.Address} has no installation.");
        var host = installation.Hosts[guest.Host];
        var workflow = guest.Workflow!;
        var stage = workflow.Machine!;
        var template = guest.Template is null ? null : repository.Templates[guest.Template];
        if (template?.Build is null)
            image = null;

        var variables = new JsonObject
        {
            ["octopushy"] = new JsonObject
            {
                ["address"] = guest.Address,
                ["stack"] = guest.Stack,
                ["lab"] = guest.Lab,
                ["installation"] = guest.Installation,
                ["revision"] = revision,
                ["workflow"] = workflow.Label,
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
            // What the guest gives its workflow, for every stage alike.
            ["inputs"] = guest.Json["inputs"]?.DeepClone() ?? new JsonObject(),
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
            ["template"] = template?.Image is { } download
                ? new JsonObject
                {
                    ["id"] = template.Id,
                    ["os"] = template.Os,
                    ["source"] = new JsonObject
                    {
                        ["url"] = download.Url,
                        ["checksum"] = download.Checksum,
                        ["checksumAlgorithm"] = download.ChecksumAlgorithm,
                        ["fileName"] = download.FileName,
                    },
                }
                : template?.Build is not null
                    ? BuiltTemplate(template, image)
                    : null,
        };

        // Where the guest's snippet goes. Its public host key joins it when a run is built, so it is never in Git;
        // the private half goes to the plan alone, as a sensitive variable. See ManagedHostKey.
        var snippets = ManagedHostKey.Wanted(repository, guest, hostKeyIssued) ? ManagedHostKey.Settings(installation) : null;
        if (snippets is not null)
        {
            var node = snippets.Hosts[guest.Host];
            variables["octopushy_snippets"] = new JsonObject
            {
                ["datastore"] = snippets.Datastore,
                ["node"] = new JsonObject { ["name"] = host.Id, ["address"] = node.Address, ["port"] = node.Port },
            };
        }

        var guestJson = guest.Json.DeepClone().AsObject();
        foreach (var field in NotProvisioned)
        {
            guestJson.Remove(field);
        }
        // How a configure run reaches the guest has no effect on the machine.
        if (guestJson["access"] is JsonObject access)
        {
            var configureOnly = ConfigureInputs.AccessOnly.Count(access.Remove);
            if (configureOnly > 0 && access.Count == 0)
            {
                guestJson.Remove("access");
            }
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
            ["workflow"] = WorkflowHash(workflow, [stage.Root]),
        };
        // A built template's image is its key: a new release of its workflow, or a new lock, plans again, as a new URL does. Not
        // the image's own digest, which is known only once built. Omitted otherwise, to preserve the hashes of downloaded templates.
        if (template?.Build is { } build)
        {
            hashed["templateBuild"] = build.Key;
        }
        // That Octopushy has a key on the machine is part of it; which version is not, so a rotation
        // plans nothing. Omitted otherwise, to preserve the hashes of existing definitions.
        if (ManagedAccess.Wanted(repository, guest, keyIssued))
        {
            hashed["managedAccess"] = true;
        }
        // That the host key is Octopushy's changes the machine, and so does where its snippet goes. Which key, and how
        // the upload logs in, do not. Omitted otherwise, as above.
        if (snippets is not null)
        {
            hashed["managedHostKey"] = new JsonObject { ["datastore"] = snippets.Datastore };
        }

        return new ProvisionInputs(guest, installation, host, workflow, stage, template, variables, Canonical.Hash(hashed)) { Image = image };
    }

    /// <summary>
    /// The variable <c>template</c> of a built template. Its <c>source</c> names the file the agent fetches before OpenTofu
    /// runs, with its digest twice: as a download's checksum would be, and as <c>sha256</c>. Without an image, the file is null.
    /// </summary>
    private static JsonObject BuiltTemplate(TemplateDefinition template, TemplateImage? image) => new()
    {
        ["id"] = template.Id,
        ["os"] = template.Os,
        ["source"] = new JsonObject
        {
            ["kind"] = "built",
            ["url"] = null,
            ["checksum"] = image?.Sha256,
            ["checksumAlgorithm"] = "sha256",
            ["fileName"] = image?.FileName,
            ["sha256"] = image?.Sha256,
            ["size"] = image?.Size,
        },
    };

    /// <summary>
    /// What a run takes from its workflow: which one, at which version, every file in its folder, and every file of the
    /// roots its stages run from. The roots are inside that folder or under <c>modules/</c>, so this is all of it.
    /// </summary>
    public static JsonObject WorkflowHash(WorkflowDefinition workflow, IEnumerable<string> roots) => new()
    {
        ["id"] = workflow.Id,
        ["version"] = workflow.Version.ToString(),
        ["files"] = TreeHash(workflow.SourceRoot, workflow.Directory),
        ["roots"] = new JsonObject(roots.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(root => KeyValuePair.Create(root, (JsonNode?)TreeHash(workflow.SourceRoot, root)))),
    };

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

    /// <summary>
    /// A hash of every file below a repository directory, by relative path and content, except
    /// <paramref name="except"/> and anything under the folder <paramref name="exceptFolder"/>.
    /// </summary>
    public static string TreeHash(string repositoryRoot, string relativeDirectory, string? except = null, string? exceptFolder = null)
    {
        var directory = Path.Combine(repositoryRoot, relativeDirectory);
        if (!Directory.Exists(directory))
        {
            return "missing";
        }

        var builder = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Select(f => (Path: Path.GetRelativePath(directory, f).Replace('\\', '/'), Full: f))
                     .Where(f => !f.Path.Split('/').Any(part => part is ".terraform") && f.Path != except
                         && (exceptFolder is null || !f.Path.StartsWith(exceptFolder + "/", StringComparison.Ordinal)))
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
