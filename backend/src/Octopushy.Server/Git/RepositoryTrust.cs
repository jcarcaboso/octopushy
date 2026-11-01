using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Server.Git;

/// <summary>
/// Which work may receive credentials. Any valid revision of the current repository source may
/// run; credentials go only to Proxmox endpoints an operator trusted for their installation, and
/// a cluster's admin password only to member addresses, and recipe code, trusted for that cluster.
/// </summary>
public static class RepositoryTrust
{
    /// <summary>The start of every reason that an installation trust resolves.</summary>
    public const string WaitingForInstallation = "Waiting for trust";

    /// <summary>The start of every reason that a cluster's trust resolves.</summary>
    public const string WaitingForCluster = "Waiting for trust of the cluster";

    public static bool IsCurrentSource(DefinitionRevision revision, RepositorySettings? settings) =>
        settings?.Url is not null && revision.SourceId is { } source && source == settings.SourceId;

    public static IQueryable<DefinitionRevision> CurrentRevisions(OctopushyDb db) =>
        db.Revisions.Where(r => r.SourceId != null && db.RepositorySettings.Any(s => s.Id == 1 && s.Url != null && s.SourceId == r.SourceId));

    public static IQueryable<DefinitionRevision> EligibleRevisions(OctopushyDb db) =>
        CurrentRevisions(db).Where(r => r.RepositoryValid);

    public static IQueryable<DefinitionRevision> HeadRevisions(OctopushyDb db) =>
        CurrentRevisions(db).Where(r => db.RepositorySettings.Any(s => s.Id == 1 && s.HeadCommit == r.Commit));

    /// <summary>The installation an address belongs to: <c>lab/installation</c> of a guest or of the installation itself.</summary>
    public static string InstallationAddress(string address) => string.Join('/', address.Split('/').Take(2));

    /// <summary>The API endpoints an installation definition declares: its connection's and any host's own, sorted.</summary>
    public static List<string> DeclaredEndpoints(JsonNode? definition) =>
        new[] { definition?["connection"]?["endpoint"]?.GetValue<string>() }
            .Concat((definition?["hosts"] as JsonObject)?.Select(h => h.Value?["endpoint"]?.GetValue<string>()) ?? [])
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    public static Task<string?> BlockedReasonAsync(OctopushyDb db, Run run, CancellationToken ct) =>
        BlockedReasonAsync(db, run.RevisionId, run.Commit, RunContext.Parse(run.Context), run.ResourceId, ct);

    /// <summary>Why work built from this revision and context, such as an observation, may not be sent yet.</summary>
    public static async Task<string?> BlockedReasonAsync(OctopushyDb db, Guid? revisionId, string? commit, RunContext context, Guid? resourceId, CancellationToken ct)
    {
        if (revisionId is not { } id || commit is null
            || !await EligibleRevisions(db).AnyAsync(r => r.Id == id && r.Commit == commit, ct))
        {
            return "Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.";
        }
        // Whatever bound them: this is what the store checks again when it releases them.
        if (await ProtectedSecrets.WithheldAsync(db, context.Secrets.ToList(), resourceId, context.CodeDigest, context.Address, ct) is { } withheld)
            return withheld;
        if (context.Cluster is { } cluster)
            return await ClusterBlockedReasonAsync(db, context.Address, cluster, resourceId, ct);
        var address = InstallationAddress(context.Address);
        var endpoint = context.Proxmox.Endpoint;
        var trust = await db.InstallationTrusts.AsNoTracking().FirstOrDefaultAsync(t => t.Address == address, ct);
        if (trust is null)
            return $"{WaitingForInstallation}: {address} is a new Proxmox installation. Trust it on its page before its credentials are sent to {endpoint}.";
        if (!trust.Endpoints.Contains(endpoint))
            return $"{WaitingForInstallation}: {endpoint} is a new endpoint for {address}. Trust the installation again on its page before its credentials are sent there.";
        return null;
    }

    private static async Task<string?> ClusterBlockedReasonAsync(OctopushyDb db, string address, ClusterContext cluster, Guid? resourceId, CancellationToken ct)
    {
        // It sends nothing and connects with no credential, and it is how the certificates to trust are found.
        if (cluster.Probe)
            return null;
        var trust = await db.ClusterTrusts.AsNoTracking().FirstOrDefaultAsync(t => t.Address == address, ct);
        if (trust is null)
            return $"{WaitingForCluster}: {address} is a new application cluster. Trust it on its page before its admin password is sent to {string.Join(", ", cluster.Endpoints)}.";
        var unknown = cluster.Endpoints.Where(e => !trust.Endpoints.Contains(e)).ToList();
        if (unknown.Count > 0)
            return $"{WaitingForCluster}: {string.Join(", ", unknown)} is a new member endpoint of {address}. Trust the cluster again on its page before its admin password is sent there.";
        if (trust.CodeDigest != cluster.CodeDigest)
            return $"{WaitingForCluster}: the recipe code of {address} or of a member, or how a member runs, changed, and it runs with the admin password. Review the change, then trust the cluster again on its page.";
        var trusted = ClusterCertificates.Parse(trust.Certificates);
        var missing = cluster.Endpoints.Where(e => !trusted.ContainsKey(e)).ToList();
        if (missing.Count > 0)
            return $"{WaitingForCluster}: no certificate is trusted yet for {string.Join(", ", missing)}. Once the member is installed, check its certificate on the cluster's page and trust the cluster again.";
        var observed = resourceId is { } id
            ? await db.Resources.AsNoTracking().Where(r => r.Id == id).Select(r => r.MemberCertificates).FirstOrDefaultAsync(ct)
            : null;
        return ClusterCertificates.ChangedReason(trust.Certificates, observed);
    }
}

/// <summary>The certificates of a cluster's members, by endpoint; the JSON kept for what was seen and what was trusted.</summary>
public sealed record MemberCertificate(string Sha256, string Pem);

public static class ClusterCertificates
{
    public static Dictionary<string, MemberCertificate> Parse(string? json) =>
        string.IsNullOrEmpty(json) ? [] : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, MemberCertificate>>(json, RunContext.Json) ?? [];

    public static string Serialize(IReadOnlyDictionary<string, MemberCertificate> certificates) =>
        System.Text.Json.JsonSerializer.Serialize(certificates, RunContext.Json);

    /// <summary>
    /// The SHA-256 of a certificate's DER, in lower-case hex, and the PEM written from that DER:
    /// null when the text is not exactly one certificate. What an agent reports as a member's
    /// fingerprint is only trusted as far as the certificate it came with says so.
    /// </summary>
    public static (string Sha256, string Pem)? Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var span = text.AsSpan();
        if (!PemEncoding.TryFind(span, out var fields) || !span[fields.Label].SequenceEqual("CERTIFICATE")
            || !span[..fields.Location.Start].IsWhiteSpace() || !span[fields.Location.End..].IsWhiteSpace())
            return null;
        try
        {
            var der = Convert.FromBase64String(span[fields.Base64Data].ToString());
            _ = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(der);
            return (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(der)), new string(PemEncoding.Write("CERTIFICATE", der)));
        }
        catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>What to tell an operator about members presenting something other than what was trusted, or null.</summary>
    public static string? ChangedReason(string? trustedJson, string? observed)
    {
        var trusted = Parse(trustedJson);
        var changed = Parse(observed).Where(c => trusted.TryGetValue(c.Key, out var known) && known.Sha256 != c.Value.Sha256)
            .Select(c => c.Key).Order(StringComparer.Ordinal).ToList();
        return changed.Count > 0
            ? $"{RepositoryTrust.WaitingForCluster}: {string.Join(", ", changed)} presents another certificate than the one trusted, so it may not be that member. If it was rebuilt, check the new certificate on the cluster's page and trust the cluster again."
            : null;
    }
}
