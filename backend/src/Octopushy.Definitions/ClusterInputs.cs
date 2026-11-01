using System.Text.Json.Nodes;

namespace Octopushy.Definitions;

/// <summary>A member with what its guest declares about where it is, and the digest of the code that provisions it.</summary>
public sealed record ClusterMemberAddress(string Guest, string Role, string Address, string Endpoint, string CodeDigest);

/// <summary>
/// Everything a cluster's run is built from. <see cref="Hash"/> changes exactly when a new plan
/// could differ. <see cref="Endpoints"/> and <see cref="CodeDigest"/> are what an operator trusts:
/// where the admin password goes, and the code that receives it: the cluster's recipe, which holds
/// it before approval, and each member's, which holds it during an apply nobody has to approve.
/// </summary>
public sealed record ClusterInputs(
    ClusterDefinition Cluster,
    RecipeDefinition Recipe,
    IReadOnlyList<ClusterMemberAddress> Members,
    JsonObject Variables,
    IReadOnlyList<string> Endpoints,
    string CodeDigest,
    string Hash,
    ReviewedCode Code)
{
    /// <summary>The port members serve their API on. Part A keeps plain HTTP off the network.</summary>
    public const int ApiPort = 53443;

    /// <summary>The oldest version whose cluster configuration sync works.</summary>
    public const string MinimumVersion = "15.6.2";

    private static readonly string[] NotPlanned = ["description", "policy", "execution"];

    public static string Endpoint(string address) => $"https://{address}:{ApiPort}";

    /// <summary>Builds the inputs of a valid cluster; the repository must have validated it.</summary>
    public static ClusterInputs Build(LabRepository repository, ClusterDefinition cluster, string revision)
    {
        var recipe = repository.Recipes[cluster.Recipe];
        var members = cluster.Members.Select(m =>
        {
            var guest = repository.Guest(m.Guest) ?? throw new InvalidOperationException($"{m.Guest} is not a guest.");
            var address = ProvisionInputs.ExpectedIpv4(guest) ?? throw new InvalidOperationException($"{m.Guest} has no static address.");
            return new ClusterMemberAddress(m.Guest, m.Role, address, Endpoint(address), repository.Recipes.TryGetValue(guest.Recipe, out var provisioned) ? ReviewedCode.Of(repository, provisioned).Digest : "missing");
        }).ToList();
        var primary = members.Single(m => m.Role == ClusterRoles.Primary);
        var settings = cluster.Json["settings"]!;
        var blocking = settings["blocking"];
        var variables = new JsonObject
        {
            ["octopushy"] = new JsonObject
            {
                ["address"] = cluster.Address,
                ["stack"] = cluster.Stack,
                ["lab"] = cluster.Lab,
                ["cluster"] = cluster.Id,
                ["revision"] = revision,
            },
            ["cluster"] = new JsonObject
            {
                ["id"] = cluster.Id,
                ["domain"] = cluster.ClusterDomain,
                ["primary"] = new JsonObject { ["guest"] = primary.Guest, ["address"] = primary.Address },
                ["secondaries"] = new JsonObject(members.Where(m => m.Role == ClusterRoles.Secondary)
                    .OrderBy(m => m.Guest, StringComparer.Ordinal)
                    .Select(m => KeyValuePair.Create(m.Guest, (JsonNode?)new JsonObject { ["address"] = m.Address }))),
                ["settings"] = new JsonObject
                {
                    ["recursion"] = new JsonObject
                    {
                        ["policy"] = settings["recursion"]?["policy"]?.GetValue<string>() ?? "AllowOnlyForPrivateNetworks",
                        ["networks"] = Strings(settings["recursion"]?["networks"]),
                    },
                    ["forwardZones"] = new JsonArray(ForwardZones(settings).Select(z => (JsonNode?)new JsonObject
                    {
                        ["zone"] = z.Zone,
                        ["forwarder"] = z.Forwarder,
                        ["protocol"] = z.Protocol,
                    }).ToArray()),
                    ["blocking"] = new JsonObject
                    {
                        ["enabled"] = blocking?["enabled"]?.GetValue<bool>() ?? true,
                        ["type"] = blocking?["type"]?.GetValue<string>() ?? "NxDomain",
                        ["updateIntervalHours"] = blocking?["updateIntervalHours"]?.GetValue<int>() ?? 24,
                        ["lists"] = Strings(blocking?["lists"]),
                        ["blocked"] = Strings(blocking?["blocked"]),
                        ["allowed"] = Strings(blocking?["allowed"]),
                    },
                },
            },
        };

        var definition = cluster.Json.DeepClone().AsObject();
        foreach (var field in NotPlanned)
        {
            definition.Remove(field);
        }

        var code = ReviewedCode.Of(repository, recipe);
        var codeDigest = Canonical.Hash(new JsonObject
        {
            ["cluster"] = code.Digest,
            ["members"] = new JsonObject(members.OrderBy(m => m.Guest, StringComparer.Ordinal)
                .Select(m => KeyValuePair.Create(m.Guest, (JsonNode?)m.CodeDigest))),
        });
        var hashed = new JsonObject
        {
            ["cluster"] = definition,
            ["members"] = new JsonArray(members.OrderBy(m => m.Guest, StringComparer.Ordinal)
                .Select(m => (JsonNode?)new JsonObject { ["guest"] = m.Guest, ["address"] = m.Address }).ToArray()),
            ["code"] = codeDigest,
        };
        return new ClusterInputs(cluster, recipe, members, variables,
            members.Select(m => m.Endpoint).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            codeDigest, Canonical.Hash(hashed), code);
    }

    public static IEnumerable<(string Zone, string Forwarder, string Protocol)> ForwardZones(JsonNode settings) =>
        (settings["forwardZones"] as JsonArray)?.Select(z =>
            (z!["zone"]!.GetValue<string>(), z["forwarder"]!.GetValue<string>(), z["protocol"]?.GetValue<string>() ?? "Udp")) ?? [];

    private static JsonArray Strings(JsonNode? list) => list?.DeepClone() as JsonArray ?? [];
}
