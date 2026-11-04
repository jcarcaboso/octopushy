using System.Text.Json.Nodes;
using Octopushy.Definitions;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// The connection an observation went through, recorded with it: the endpoint, whether TLS was verified, and the store key of
/// the CA it was pinned to. An observation is evidence about an installation only while the installation still connects the
/// same way: one taken through an endpoint it no longer has says nothing about the machines behind the new one.
/// </summary>
public static class ObservedConnection
{
    public static JsonObject Of(string endpoint, bool verifyTls, string? caCertificateKey) => new()
    {
        ["endpoint"] = Normalize(endpoint),
        ["verifyTls"] = verifyTls,
        ["caCertificate"] = caCertificateKey,
    };

    /// <summary>Whether <paramref name="recorded"/> is the connection <paramref name="connection"/> declares.</summary>
    public static bool Matches(JsonNode? recorded, ConnectionDefinition connection) =>
        recorded is JsonObject o
        && o["endpoint"]?.ToString() == Normalize(connection.Endpoint)
        && o["verifyTls"] is JsonValue v && v.TryGetValue<bool>(out var verify) && verify == connection.VerifyTls
        && o["caCertificate"]?.ToString() == connection.CaCertificate?.Key;

    public static string Describe(JsonNode? recorded) => recorded is JsonObject o ? o["endpoint"]?.ToString() ?? "an unknown endpoint" : "an unrecorded connection";

    private static string Normalize(string endpoint) => endpoint.Trim().TrimEnd('/');
}
