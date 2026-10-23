using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Store;

namespace Octopushy.Server.Orchestration;

/// <summary>Resolves metadata once per run, then checks the same versions before every release.</summary>
internal static class CredentialBindings
{
    // CA certificates are public trust material. Other current bindings feed credential channels.
    internal static bool RequiresSecret(string name) => name != SecretNames.ProxmoxCa;

    internal static List<string> Validate(RunContext context)
    {
        if (context.Secrets.ContainsKey(SecretNames.ProxmoxToken)
            || context.SecretVersions.ContainsKey(SecretNames.ProxmoxToken)
            || context.RequestedSecretVersions.ContainsKey(SecretNames.ProxmoxToken))
            return ["Legacy Proxmox token context cannot be upgraded automatically; inspect and replan with explicit read-only and privileged credentials"];

        var problems = new List<string>();
        if (!context.Secrets.ContainsKey(SecretNames.ProxmoxReadOnlyToken))
            problems.Add("Missing read-only Proxmox role; inspect and replan");
        if (context.OpenTofu is not null && !context.Secrets.ContainsKey(SecretNames.ProxmoxPrivilegedToken))
            problems.Add("Missing privileged Proxmox role; inspect and replan");
        if (context.Secrets.TryGetValue(SecretNames.ProxmoxPrivilegedToken, out var writer)
            && context.Secrets.Any(s => s.Key != SecretNames.ProxmoxPrivilegedToken && s.Value == writer))
            problems.Add("A privileged Proxmox token requires a distinct store key and cannot be aliased to another role");

        if (context.OpenTofu is { } tofu)
        {
            foreach (var (name, value) in tofu.Env)
            {
                if (name.StartsWith("PROXMOX_", StringComparison.Ordinal) && !(name switch
                {
                    "PROXMOX_VE_ENDPOINT" => value == context.Proxmox.Endpoint,
                    "PROXMOX_VE_INSECURE" => value == (context.Proxmox.VerifyTls ? "false" : "true"),
                    _ => false,
                }))
                    problems.Add($"Proxmox provider environment '{name}' is reserved; inspect and replan");
            }
            if (tofu.SecretEnv.GetValueOrDefault("PROXMOX_VE_API_TOKEN") != SecretNames.ProxmoxToken)
                problems.Add("Proxmox provider token mapping is reserved; inspect and replan");
            foreach (var (name, role) in tofu.SecretEnv)
            {
                if (name == "PROXMOX_VE_API_TOKEN" && role == SecretNames.ProxmoxToken)
                    continue;
                if (name.StartsWith("PROXMOX_", StringComparison.Ordinal) || role != SecretNames.Backend(name)
                    || !context.Secrets.ContainsKey(role))
                    problems.Add($"Backend environment '{name}' cannot override reserved Proxmox roles; inspect and replan");
            }
        }
        return problems;
    }

    internal static async Task<List<string>> CheckAsync(
        OctopushyDb db, Run run, RunContext context, CancellationToken ct, IReadOnlyCollection<string>? neededNames = null)
    {
        var problems = Validate(context);
        if (neededNames is not null)
            problems.AddRange(neededNames.Where(n => !context.Secrets.ContainsKey(n)).Select(n => $"Missing credential role {n}; inspect and replan"));
        if (problems.Count > 0)
            return problems;
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
