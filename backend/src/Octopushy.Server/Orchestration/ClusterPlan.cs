using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// What a cluster run decides from evidence: whether its members are ready, what the plan must
/// show about them, and which members the approved plan changed. Members and block lists are
/// resources of the cluster's root, so a change to them is in the preview and follows the
/// approval and destructive policy like any other.
/// </summary>
public static partial class ClusterPlan
{
    private const string SecondaryPrefix = "terraform_data.secondary[";
    private const string UndeclaredEntries = "terraform_data.undeclared_entries";

    /// <summary>
    /// Why a run may not go on, for the two ways it can touch one member at once. A cluster run
    /// that has not started waits for its members. Once started it holds them until it ends,
    /// also when its outcome is uncertain, so a guest's run waits for it. The rule is on runs,
    /// so a queued attempt cannot get past it, and it cannot deadlock: only a run that has not
    /// started ever waits, and what it waits for has.
    /// </summary>
    public static Task<string?> BlockedReasonAsync(OctopushyDb db, Run run, CancellationToken ct) =>
        run.Operation == Operations.Cluster
            ? run.StartedAt is null ? MembersNotReadyAsync(db, run, ct) : Task.FromResult<string?>(null)
            : GuestBlockedAsync(db, run, ct);

    private static async Task<string?> MembersNotReadyAsync(OctopushyDb db, Run run, CancellationToken ct)
    {
        var cluster = RunContext.Parse(run.Context).Cluster!;
        var guests = cluster.Members.Select(m => m.Guest).ToList();
        var resources = await db.Resources.AsNoTracking().Where(r => r.Kind == ResourceKinds.Guest && guests.Contains(r.Address)).ToListAsync(ct);
        // A guest on its way out is still in the cluster until this run takes it out, and its
        // retirement waits for that, so only what it has really started counts against it.
        var leaving = await db.ClusterBindings.AsNoTracking()
            .Where(b => b.ClusterId == run.ResourceId && b.DetachedAt == null && !guests.Contains(b.Guest))
            .Select(b => new { b.GuestId, b.Guest }).ToListAsync(ct);
        var ids = resources.Select(r => r.Id).Concat(leaving.Select(b => b.GuestId)).ToList();
        var active = await db.Runs.AsNoTracking()
            .Where(r => ids.Contains(r.ResourceId) && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
            .Select(r => new { r.ResourceId, r.Number, r.Status, r.StartedAt })
            .ToListAsync(ct);
        var waiting = new List<string>();
        foreach (var guest in guests)
        {
            var resource = resources.Where(r => r.RetiredAt is null).FirstOrDefault(r => r.Address == guest);
            if (resource is null)
                waiting.Add($"{guest} does not exist yet");
            else if (active.FirstOrDefault(r => r.ResourceId == resource.Id) is { } running)
                waiting.Add($"{guest} has run #{running.Number}, which is {running.Status}");
            else if (!resource.Valid || resource.MissingSince is not null)
                waiting.Add($"{guest} has no valid definition");
            else if (resource.SyncedInputsHash is null || resource.SyncedInputsHash != resource.DesiredInputsHash)
                waiting.Add($"{guest} is not applied from its current definition");
            else if (resource.VerifiedAt is null || resource.AppliedAt > resource.VerifiedAt)
                waiting.Add($"{guest} is not verified since its last apply");
        }

        foreach (var bound in leaving)
        {
            if (active.FirstOrDefault(r => r.ResourceId == bound.GuestId && r.StartedAt != null) is { } running)
                waiting.Add($"{bound.Guest} has run #{running.Number}, which is {running.Status}");
        }

        return waiting.Count == 0 ? null : $"Waiting for member guests: {string.Join("; ", waiting)}.";
    }

    private static async Task<string?> GuestBlockedAsync(OctopushyDb db, Run run, CancellationToken ct)
    {
        var bound = await db.ClusterBindings.AsNoTracking().Where(b => b.GuestId == run.ResourceId && b.DetachedAt == null).ToListAsync(ct);
        if (run.Operation == Operations.Retire && bound.Count > 0)
        {
            var clusters = await db.Resources.AsNoTracking().Where(r => bound.Select(b => b.ClusterId).Contains(r.Id)).Select(r => r.Address).ToListAsync(ct);
            if (bound.All(b => b.UnconfirmedAt is not null))
                return $"Waiting for the guest to leave: {string.Join(", ", clusters)} no longer lists it, but nothing saw it leave, and it may still believe it is a member. "
                    + "Check it, then acknowledge that on the cluster's page.";
            return $"Waiting for the guest to leave: it is still joined to {string.Join(", ", clusters)}. "
                + "Take it out of members and let that cluster's run finish, or, if it was taken out by hand, acknowledge that on the cluster's page.";
        }

        var clusterRuns = await db.Runs.AsNoTracking()
            .Where(r => r.Operation == Operations.Cluster && r.StartedAt != null
                        && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
            .Select(r => new { r.ResourceId, r.Number, r.Context })
            .ToListAsync(ct);
        if (clusterRuns.Count == 0)
            return null;
        var address = await db.Resources.AsNoTracking().Where(r => r.Id == run.ResourceId).Select(r => r.Address).FirstOrDefaultAsync(ct);
        foreach (var holder in clusterRuns)
        {
            if (bound.Any(b => b.ClusterId == holder.ResourceId)
                || RunContext.Parse(holder.Context).Cluster?.Members.Any(m => m.Guest == address) == true)
                return $"Waiting for cluster run #{holder.Number}, which is working on this guest.";
        }

        return null;
    }

    /// <summary>
    /// Records every member before the apply that may form or join it, so that from here on a
    /// guest cannot be retired unnoticed, whatever becomes of the run or of cluster.yaml.
    /// </summary>
    public static async Task BindMembersAsync(OctopushyDb db, Run run, ClusterContext cluster, DateTimeOffset now, CancellationToken ct)
    {
        var guests = cluster.Members.Select(m => m.Guest).ToList();
        var resources = await db.Resources.Where(r => r.Kind == ResourceKinds.Guest && r.RetiredAt == null && guests.Contains(r.Address)).ToListAsync(ct);
        var bindings = await db.ClusterBindings.Where(b => b.ClusterId == run.ResourceId).ToListAsync(ct);
        foreach (var member in cluster.Members)
        {
            var guest = resources.FirstOrDefault(r => r.Address == member.Guest);
            if (guest is null)
                continue;
            var binding = bindings.FirstOrDefault(b => b.GuestId == guest.Id);
            if (binding is null)
            {
                binding = new ClusterBinding { ClusterId = run.ResourceId, GuestId = guest.Id, Guest = member.Guest, Role = member.Role, Address = member.Address, BoundAt = now };
                db.ClusterBindings.Add(binding);
            }

            // A guest that left and is declared again is a new member: the peer it was says nothing
            // about the one it will be, and removal must not match the old one.
            if (binding.DetachedAt is not null)
            {
                binding.PeerId = null;
                binding.PeerName = null;
            }

            binding.Guest = member.Guest;
            binding.Role = member.Role;
            binding.Address = member.Address;
            binding.DetachedAt = null;
            binding.DetachedBy = null;
            binding.DetachNote = null;
            binding.UnconfirmedAt = null;
        }
    }

    /// <summary>Keeps what identifies each member on the primary, from what an agent reported.</summary>
    public static async Task RecordPeersAsync(OctopushyDb db, Run run, JsonNode? outputs, CancellationToken ct)
    {
        var bindings = await db.ClusterBindings.Where(b => b.ClusterId == run.ResourceId).ToListAsync(ct);
        foreach (var member in (outputs?["members"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var binding = bindings.FirstOrDefault(b => b.Guest == member["guest"]?.GetValue<string>());
            if (binding is null || member["peerId"] is not JsonValue id || !id.TryGetValue<long>(out var peerId))
                continue;
            binding.PeerId = peerId;
            binding.PeerName = member["peerName"]?.GetValue<string>() ?? binding.PeerName;
        }
    }

    /// <summary>
    /// The peers the plan's removals stand for, as they were bound: never a count of what looks
    /// undeclared. A guest that kept its binding under another address was renamed, not removed.
    /// </summary>
    public static async Task<JsonArray> RemovalsAsync(OctopushyDb db, Run run, JsonNode? plan, CancellationToken ct)
    {
        var removed = Removals(plan);
        var bindings = await db.ClusterBindings.AsNoTracking()
            .Where(b => b.ClusterId == run.ResourceId && b.DetachedAt == null && removed.Contains(b.Guest)).ToListAsync(ct);
        var ids = bindings.Select(b => b.GuestId).ToList();
        var current = await db.Resources.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Address, ct);
        var members = RunContext.Parse(run.Context).Cluster!.Members.Select(m => m.Guest).ToHashSet(StringComparer.Ordinal);
        return new JsonArray(bindings.Where(b => !members.Contains(current.GetValueOrDefault(b.GuestId, b.Guest)))
            .OrderBy(b => b.Guest, StringComparer.Ordinal)
            .Select(b => (JsonNode?)new JsonObject
            {
                ["guest"] = b.Guest,
                ["address"] = b.Address,
                ["peerId"] = b.PeerId,
                ["peerName"] = b.PeerName,
            }).ToArray());
    }

    /// <summary>
    /// Marks the guests a run saw leave, which is what lets them be retired. That the primary no
    /// longer lists one is not the same thing: it may never have been told, so it stays bound
    /// until an operator has looked.
    /// </summary>
    public static async Task RecordDetachedAsync(OctopushyDb db, Run run, JsonNode? outputs, DateTimeOffset now, CancellationToken ct)
    {
        List<string?> Named(string field) => (outputs?[field] as JsonArray)?.Select(g => g?.GetValue<string>()).ToList() ?? [];
        var (left, unconfirmed) = (Named("detached"), Named("unconfirmed"));
        foreach (var binding in await db.ClusterBindings.Where(b => b.ClusterId == run.ResourceId && b.DetachedAt == null).ToListAsync(ct))
        {
            if (left.Contains(binding.Guest))
            {
                binding.DetachedAt = now;
                binding.DetachedBy = $"run #{run.Number}";
            }
            else if (unconfirmed.Contains(binding.Guest))
            {
                binding.UnconfirmedAt = now;
            }
        }
    }

    /// <summary>
    /// What the plan has to replace for what the agent found: a secondary that is out of the
    /// cluster, so that joining it is in the preview, and entries nobody declared.
    /// </summary>
    public static IReadOnlyList<string> Replacements(JsonNode? inspection)
    {
        var replace = (inspection?["members"] as JsonArray)?.OfType<JsonObject>()
            .Where(m => m["state"]?.GetValue<string>() == "absent")
            .Select(m => $"{SecondaryPrefix}\"{m["guest"]!.GetValue<string>()}\"]")
            .ToList() ?? [];
        if ((inspection?["undeclared"]?["blocked"] as JsonArray)?.Count > 0 || (inspection?["undeclared"]?["allowed"] as JsonArray)?.Count > 0)
            replace.Add(UndeclaredEntries);
        return replace;
    }

    /// <summary>The guests whose record the plan creates, replaces, or updates.</summary>
    public static IReadOnlyList<string> Joins(JsonNode? plan) =>
        Secondaries(plan).Where(s => !s.Removed).Select(s => s.Guest).ToList();

    /// <summary>The guests whose record the plan deletes: an approved removal from the cluster.</summary>
    public static IReadOnlyList<string> Removals(JsonNode? plan) =>
        Secondaries(plan).Where(s => s.Removed).Select(s => s.Guest).ToList();

    /// <summary>
    /// Whether the plan removes entries from a live server. The resource that does it is also
    /// created on a first run, when there is nothing to remove, so its being in the plan is not
    /// enough: inspection has to have named entries, or the plan has to delete or replace it.
    /// </summary>
    public static bool PrunesEntries(JsonNode? plan, JsonNode? inspection) =>
        Addresses(plan).Any(c => c.Address == UndeclaredEntries
            && (c.Actions.Contains("delete") || UndeclaredEntriesIn(inspection).Count > 0));

    /// <summary>What inspection found in one list that nobody declared, as it was approved.</summary>
    public static JsonArray Undeclared(JsonNode? inspection, string list) =>
        new((inspection?["undeclared"]?[list] as JsonArray)?.Select(e => e?.DeepClone()).ToArray() ?? []);

    /// <summary>What inspection found that nobody declared, to name in an approval.</summary>
    public static IReadOnlyList<string> UndeclaredEntriesIn(JsonNode? inspection) =>
        new[] { "blocked", "allowed" }.SelectMany(l => (inspection?["undeclared"]?[l] as JsonArray)?.Select(e => e?.GetValue<string>() ?? "") ?? []).ToList();

    /// <summary>
    /// Whether what is declared was downloaded. Nothing was declared and nothing was ever
    /// downloaded also counts: a cluster with no lists has nothing to fetch.
    /// </summary>
    public static bool ListsDownloaded(Resource cluster, ClusterContext context) =>
        cluster.BlockListsRefreshed is { } refreshed ? refreshed == context.BlockListsHash : context.BlockListsHash == EmptyListsHash;

    private static readonly string EmptyListsHash = Definitions.Canonical.Hash(new JsonArray());

    private static IEnumerable<(string Guest, bool Removed)> Secondaries(JsonNode? plan) =>
        Addresses(plan).Select(c => (Match: SecondaryAddress().Match(c.Address), c.Actions))
            .Where(c => c.Match.Success)
            .Select(c => (c.Match.Groups[1].Value, c.Actions.Count == 1 && c.Actions[0] == "delete"));

    private static IEnumerable<(string Address, List<string> Actions)> Addresses(JsonNode? plan) =>
        (plan?["changes"] as JsonArray)?.OfType<JsonObject>().Select(c => (
            c["address"]?.GetValue<string>() ?? "",
            (c["actions"] as JsonArray)?.Select(a => a?.GetValue<string>() ?? "").ToList() ?? [])) ?? [];

    [GeneratedRegex("""^terraform_data\.secondary\["([^"]+)"\]$""")]
    private static partial Regex SecondaryAddress();
}
