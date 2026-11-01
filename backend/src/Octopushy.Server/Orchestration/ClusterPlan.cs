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
    private const string BlockLists = "terraform_data.block_lists";
    private const string UndeclaredEntries = "terraform_data.undeclared_entries";

    /// <summary>
    /// Why a cluster run waits for its members, naming only the ones not ready, so that one
    /// member's trouble reads as that member's. Null when every guest is applied and verified.
    /// </summary>
    public static async Task<string?> BlockedReasonAsync(OctopushyDb db, Run run, CancellationToken ct)
    {
        if (run.Operation != Operations.Cluster)
            return null;
        var cluster = RunContext.Parse(run.Context).Cluster!;
        var guests = cluster.Members.Select(m => m.Guest).ToList();
        var resources = await db.Resources.AsNoTracking().Where(r => r.Kind == ResourceKinds.Guest && guests.Contains(r.Address)).ToListAsync(ct);
        var ids = resources.Select(r => r.Id).ToList();
        var active = await db.Runs.AsNoTracking()
            .Where(r => ids.Contains(r.ResourceId) && r.Status != RunStatuses.Succeeded && r.Status != RunStatuses.Failed && r.Status != RunStatuses.Cancelled)
            .Select(r => new { r.ResourceId, r.Number, r.Status })
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

        return waiting.Count == 0 ? null : $"Waiting for member guests: {string.Join("; ", waiting)}.";
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

    public static bool PrunesEntries(JsonNode? plan) => Addresses(plan).Any(c => c.Address == UndeclaredEntries);

    public static bool ChangesBlockLists(JsonNode? plan) => Addresses(plan).Any(c => c.Address == BlockLists);

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
