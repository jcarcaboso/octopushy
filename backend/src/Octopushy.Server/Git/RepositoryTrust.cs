using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Server.Git;

/// <summary>
/// Which work may receive credentials. Any valid revision of the current repository source may
/// run; credentials go only to Proxmox endpoints an operator trusted for their installation.
/// </summary>
public static class RepositoryTrust
{
    /// <summary>The start of every reason that an installation trust resolves.</summary>
    public const string WaitingForInstallation = "Waiting for trust";

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
        BlockedReasonAsync(db, run.RevisionId, run.Commit, RunContext.Parse(run.Context), ct);

    /// <summary>Why work built from this revision and context, such as an observation, may not be sent yet.</summary>
    public static async Task<string?> BlockedReasonAsync(OctopushyDb db, Guid? revisionId, string? commit, RunContext context, CancellationToken ct)
    {
        if (revisionId is not { } id || commit is null
            || !await EligibleRevisions(db).AnyAsync(r => r.Id == id && r.Commit == commit, ct))
        {
            return "Waiting for the current repository source: this work was built from a previous source or an invalid revision. Plan it again.";
        }
        var address = InstallationAddress(context.Address);
        var endpoint = context.Proxmox.Endpoint;
        var trust = await db.InstallationTrusts.AsNoTracking().FirstOrDefaultAsync(t => t.Address == address, ct);
        if (trust is null)
            return $"{WaitingForInstallation}: {address} is a new Proxmox installation. Trust it on its page before its credentials are sent to {endpoint}.";
        if (!trust.Endpoints.Contains(endpoint))
            return $"{WaitingForInstallation}: {endpoint} is a new endpoint for {address}. Trust the installation again on its page before its credentials are sent there.";
        return null;
    }
}
