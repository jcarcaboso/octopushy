using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Store;

namespace Octopushy.Server.Orchestration;

/// <summary>Resolves metadata once per run, then checks the same versions before every release.</summary>
internal static class CredentialBindings
{
    // CA certificates are public trust material. Other current bindings feed credential channels.
    internal static bool RequiresSecret(string name) => name != SecretNames.ProxmoxCa;

    internal static async Task<List<string>> CheckAsync(
        OctopushyDb db, Run run, RunContext context, CancellationToken ct, IReadOnlyCollection<string>? neededNames = null)
    {
        var problems = new List<string>();
        // Pin every binding initially, but later steps need only their own credentials:
        // revoking a completed apply's backend credential must not prevent guest verification.
        var bindings = context.Secrets.Where(s => !context.SecretBindingsPinned || neededNames is null || neededNames.Contains(s.Key)).ToList();
        var keys = bindings.Select(s => s.Value).Distinct().ToArray();
        var entries = await db.StoreEntries.AsNoTracking().Where(e => keys.Contains(e.Key)).ToDictionaryAsync(e => e.Key, ct);
        var versions = await db.StoreVersions.AsNoTracking().Where(v => keys.Contains(v.Key)).ToListAsync(ct);
        var selected = new Dictionary<string, int>(StringComparer.Ordinal);

        // Recover old runs from release evidence, never from today's current pointer.
        var audit = context.SecretBindingsPinned ? [] : await db.SecretResolutions.AsNoTracking()
            .Where(r => db.Attempts.Any(a => a.Id == r.AttemptId && a.RunId == run.Id)).ToListAsync(ct);
        var previouslyDispatched = !context.SecretBindingsPinned && (audit.Count > 0 ||
            await db.Attempts.AsNoTracking().AnyAsync(a => a.RunId == run.Id &&
                (a.AssignedAt != null || a.StartedAt != null || a.FinishedAt != null), ct) ||
            await db.Steps.AsNoTracking().AnyAsync(s => s.RunId == run.Id && s.StartedAt != null, ct));

        foreach (var (name, key) in bindings)
        {
            if (key.StartsWith(VariableStore.SystemScope, StringComparison.Ordinal))
            {
                problems.Add($"{key}: reserved system scope");
                continue;
            }

            if (!entries.TryGetValue(key, out var entry))
            {
                problems.Add($"{key}: missing");
                continue;
            }

            if (RequiresSecret(name) && entry.Visibility != Visibilities.Secret)
            {
                problems.Add($"{key}: credential must be stored as secret, not plain");
                continue;
            }

            int version;
            if (context.SecretBindingsPinned)
            {
                if (!context.SecretVersions.TryGetValue(name, out version))
                {
                    problems.Add($"{key}: pinned version is missing; reconcile this run");
                    continue;
                }
            }
            else
            {
                var historical = audit.Where(a => a.Key == key).Select(a => a.Version).Distinct().ToArray();
                if (historical.Length > 1 || (previouslyDispatched && historical.Length == 0))
                {
                    problems.Add($"{key}: legacy run has no unambiguous version evidence; reconcile before starting a new run");
                    continue;
                }

                version = context.RequestedSecretVersions.GetValueOrDefault(name,
                    historical.Length == 1 ? historical[0] : entry.CurrentVersion);
                if (historical.Length == 1 && historical[0] != version)
                {
                    problems.Add($"{key}: requested version conflicts with execution history");
                    continue;
                }
            }

            var value = versions.SingleOrDefault(v => v.Key == key && v.Version == version);
            if (value is null || value.RevokedAt is not null || value.ActivatedAt is null)
            {
                problems.Add($"{key} v{version}: {(value is null ? "missing" : value.RevokedAt is not null ? "revoked" : "candidate not promoted")}");
                continue;
            }

            selected[name] = version;
        }

        // All-or-nothing: missing prerequisites must not pin only half of a future run.
        if (problems.Count == 0 && !context.SecretBindingsPinned)
        {
            context.SecretVersions.Clear();
            foreach (var (name, version) in selected)
            {
                context.SecretVersions.Add(name, version);
            }

            context.SecretBindingsPinned = true;
            run.Context = context.Serialize();
        }

        return problems;
    }
}
