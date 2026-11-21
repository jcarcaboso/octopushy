using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// What a guest's setup playbooks report, and what its peers get of it. A workflow declares the values with <c>reports</c>, each
/// with a pattern, and the guests whose reports a guest gets with <c>peers</c>. A value is kept only from a playbook step of
/// the guest's own configure run that ended as the playbook meant, which reached the guest over its pinned host key: so a
/// value is as trustworthy as that pin, and no value is ever read from anywhere else. See docs/workflows.md#reports-and-peers.
/// </summary>
public static class GuestReports
{
    /// <summary>
    /// The guests <paramref name="guest"/>'s workflow names as its peers: guests of the same lab whose id a value at the
    /// workflow's <c>peers</c> path names, valid, not retired, and whose workflow has the same id. Never the guest itself, and
    /// never an id two guests of the lab share.
    /// </summary>
    public static IReadOnlyList<GuestDefinition> PeersOf(LabRepository repository, GuestDefinition guest)
    {
        if (guest.Workflow?.Peers is not { } path)
            return [];
        var named = InputPath.Strings(new JsonObject { ["inputs"] = guest.Json["inputs"]?.DeepClone() }, path)
            .Where(id => id != guest.Id).ToHashSet(StringComparer.Ordinal);
        // An id is unique in an installation, not in a lab: one that two guests share names neither.
        return repository.Guests
            .Where(g => g.Lab == guest.Lab && named.Contains(g.Id))
            .GroupBy(g => g.Id, StringComparer.Ordinal)
            .Where(same => same.Count() == 1)
            .Select(same => same.Single())
            .Where(g => !g.Retired && g.Workflow?.Id == guest.Workflow.Id && repository.Resource(g.Address)?.Valid == true)
            .OrderBy(g => g.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// What <paramref name="guest"/>'s playbooks get in <c>octopushy.peers</c>: for each of its <see cref="PeersOf">peers</see>,
    /// by id, the address its setup connects to and the values it reported. A value is given only when the guest's own workflow
    /// declares it and its pattern still matches, and only when it was reported over a connection to that same address: a
    /// value read at an address the peer no longer has says nothing about the peer now. Null when the workflow names no peers.
    /// </summary>
    public static async Task<JsonObject?> PeersAsync(OctopushyDb db, LabRepository repository, GuestDefinition guest, CancellationToken cancellationToken)
    {
        if (guest.Workflow?.Peers is null)
            return null;
        var peers = PeersOf(repository, guest);
        var addresses = peers.Select(p => p.Address).ToList();
        var stored = await db.Resources.AsNoTracking()
            .Where(r => r.Kind == ResourceKinds.Guest && addresses.Contains(r.Address) && r.RetiredAt == null && r.MissingSince == null)
            .Join(db.GuestConfigurations.AsNoTracking(), r => r.Id, c => c.ResourceId, (r, c) => new { r.Address, c.Reports })
            .ToDictionaryAsync(x => x.Address, x => x.Reports, StringComparer.Ordinal, cancellationToken);
        var declared = guest.Workflow.Reports;
        var result = new JsonObject();
        foreach (var peer in peers)
        {
            var address = ConfigureInputs.AddressOf(peer);
            var reports = new JsonObject();
            var kept = stored.GetValueOrDefault(peer.Address) is { } text ? JsonNode.Parse(text) as JsonObject : null;
            foreach (var (name, report) in declared.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                if (kept?[name] is JsonObject entry && entry["value"]?.GetValue<string>() is { } value
                    && address is not null && entry["address"]?.GetValue<string>() == address && report.Accepts(value))
                    reports[name] = value;
            }

            result[peer.Id] = new JsonObject { ["address"] = address, ["reports"] = reports };
        }

        return result;
    }

    /// <summary>
    /// The peers whose part of <paramref name="current"/> differs from what <paramref name="recorded"/> held, as
    /// <c>dns-02 (certificate_sha256)</c>, or with no names for a peer that came or went. Empty when nothing changed.
    /// </summary>
    public static IReadOnlyList<string> Changes(string? recorded, JsonObject? current)
    {
        var before = recorded is null ? null : JsonNode.Parse(recorded) as JsonObject;
        var changes = new List<string>();
        var ids = (before?.Select(p => p.Key) ?? []).Union(current?.Select(p => p.Key) ?? [], StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var was = before?[id];
            var now = current?[id];
            if (JsonNode.DeepEquals(was, now))
                continue;
            if (was is null || now is null || !JsonNode.DeepEquals(was["address"], now["address"]))
            {
                changes.Add(id);
                continue;
            }

            var names = ((was["reports"] as JsonObject)?.Select(p => p.Key) ?? []).Union((now["reports"] as JsonObject)?.Select(p => p.Key) ?? [], StringComparer.Ordinal)
                .Where(n => !JsonNode.DeepEquals(was["reports"]?[n], now["reports"]?[n])).Order(StringComparer.Ordinal);
            changes.Add($"{id} ({string.Join(", ", names)})");
        }

        return changes;
    }

    /// <summary>
    /// Keeps what a finished playbook step reported, by name, over what an earlier run reported: only names the run's workflow
    /// declared when the run was made, whose value matches the pattern whole. Returns the names that were not kept, for the
    /// step to say so; a value is never repeated, since only a declared name is known not to be a secret.
    /// </summary>
    public static IReadOnlyList<string> Record(GuestConfiguration configuration, AnsibleContext ansible, JsonNode? outputs, Guid runId, DateTimeOffset now)
    {
        if (outputs?["reports"] is not JsonObject reported || reported.Count == 0)
            return [];
        var declared = ansible.Reports ?? new Dictionary<string, string>();
        var kept = configuration.Reports is { } text ? JsonNode.Parse(text) as JsonObject ?? [] : [];
        var refused = new List<string>();
        foreach (var (name, node) in reported)
        {
            var value = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (!declared.TryGetValue(name, out var pattern) || value is null
                || !new WorkflowReport(name, null, pattern).Accepts(value))
            {
                refused.Add(declared.ContainsKey(name) ? name : "a name the workflow does not declare");
                continue;
            }

            kept[name] = new JsonObject
            {
                ["value"] = value,
                ["address"] = ansible.Address,
                ["run"] = runId.ToString(),
                ["at"] = now.ToString("O"),
            };
        }

        configuration.Reports = kept.Count == 0 ? null : kept.ToJsonString();
        return refused.Distinct(StringComparer.Ordinal).ToList();
    }
}
