using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Data;
using Octopushy.Server.Store;

namespace Octopushy.Server.Orchestration;

/// <summary>Resolves metadata once per run, then checks the same versions before every release.</summary>
internal static class CredentialBindings
{
    // CA certificates are public trust material. Other current bindings feed credential channels.
    internal static bool RequiresSecret(string name) => RunContext.BareRole(name) is not (SecretNames.ProxmoxCa or SecretNames.AnsibleHostKey);

    internal static List<string> Validate(RunContext context)
    {
        if (context.Stages is { } stages)
            return stages.SelectMany(s => Validate(context.ForStage(s.Name))).ToList();
        if (context.Ansible is not null)
            return ValidateAnsible(context);
        if (context.Stage is not null)
            return ValidateOpenTofuStage(context);

        // The executor's alias for whichever token a step holds; a run binds the role itself.
        if (context.Secrets.ContainsKey(SecretNames.ProxmoxToken)
            || context.SecretVersions.ContainsKey(SecretNames.ProxmoxToken)
            || context.RequestedSecretVersions.ContainsKey(SecretNames.ProxmoxToken))
            return ["The Proxmox token alias is not a credential role; inspect and replan with a shared token or explicit read-only and privileged credentials"];

        var problems = new List<string>();
        var roles = context.Secrets.Keys.Concat(context.SecretVersions.Keys).Concat(context.RequestedSecretVersions.Keys).ToHashSet(StringComparer.Ordinal);
        if (roles.Contains(SecretNames.ProxmoxSharedToken)
            && (roles.Contains(SecretNames.ProxmoxReadOnlyToken) || roles.Contains(SecretNames.ProxmoxPrivilegedToken)))
            problems.Add("Shared and split Proxmox roles cannot be mixed; inspect and replan");
        var shared = context.Secrets.ContainsKey(SecretNames.ProxmoxSharedToken);
        if (context.Secrets.TryGetValue(SecretNames.ProxmoxSharedToken, out var sharedKey)
            && context.Secrets.Any(s => s.Key != SecretNames.ProxmoxSharedToken && s.Value == sharedKey))
            problems.Add("A shared Proxmox token requires a distinct store key and cannot be aliased to another role");
        if (!shared && !context.Secrets.ContainsKey(SecretNames.ProxmoxReadOnlyToken))
            problems.Add("Missing read-only Proxmox role; inspect and replan");
        if (!shared && context.OpenTofu is not null && !context.Secrets.ContainsKey(SecretNames.ProxmoxPrivilegedToken))
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
            foreach (var (name, _) in tofu.SecretEnv.Where(e => e.Key != "PROXMOX_VE_API_TOKEN"))
                problems.Add($"OpenTofu secret environment '{name}' is not allowed; only the Proxmox token is released; inspect and replan");
        }
        return problems;
    }

    /// <summary>
    /// The keys a playbook may not hold, whatever today's definitions say: a removed installation's
    /// token stays stored, and its key stops looking like one. So this asks what any installation
    /// last declared, including those that are gone, and what a key has ever been released to.
    /// </summary>
    private static async Task<HashSet<string>> ProxmoxKeysAsync(OctopushyDb db, string[] keys, CancellationToken ct)
    {
        var released = await db.SecretResolutions.AsNoTracking()
            .Where(r => keys.Contains(r.Key) && !db.Attempts.Any(a => a.Id == r.AttemptId && a.Executor == "ansible"))
            .Select(r => r.Key).Distinct().ToListAsync(ct);
        var definitions = await db.Resources.AsNoTracking()
            .Where(r => r.Kind == ResourceKinds.Installation && r.Definition != null)
            .Select(r => r.Definition!).ToListAsync(ct);
        var declared = definitions.Select(d => JsonNode.Parse(d)?["connection"])
            .SelectMany(c => new[] { c?["token"], c?["readOnlyToken"], c?["privilegedToken"] })
            .Select(reference => reference?["store"]?.GetValue<string>()).OfType<string>();
        return [.. released, .. declared];
    }

    /// <summary>A configure run holds its SSH key, its pin and its own secrets: nothing that reaches Proxmox.</summary>
    private static List<string> ValidateAnsible(RunContext context)
    {
        var problems = new List<string>();
        if (context.Secrets.Keys.Any(n => n is SecretNames.ProxmoxSharedToken or SecretNames.ProxmoxReadOnlyToken or SecretNames.ProxmoxPrivilegedToken or SecretNames.ProxmoxToken))
            problems.Add("A configure run carries no Proxmox credentials; inspect and replan");
        // A rotation takes the guest's own key by step, so it binds only the pin.
        if ((context.AccessRotation is null && !context.Secrets.ContainsKey(SecretNames.AnsibleSshKey)) || !context.Secrets.ContainsKey(SecretNames.AnsibleHostKey))
            problems.Add("A configure run needs an SSH key and a pinned host key; inspect and replan");
        return problems;
    }

    /// <summary>An OpenTofu stage holds nothing yet: no token, no secret environment, no secret of any kind.</summary>
    private static List<string> ValidateOpenTofuStage(RunContext context) =>
        context.Secrets.Count == 0 && context.OpenTofu is { SecretEnv.Count: 0 } tofu && tofu.Env.Keys.All(n => !n.StartsWith("PROXMOX_", StringComparison.Ordinal))
            ? [] : ["An OpenTofu stage carries no credentials; inspect and replan"];

    /// <summary>
    /// Checks and pins the context's credential versions. An observation passes no run: it has
    /// no history to recover, and the caller stores the pinned context on its attempt.
    /// </summary>
    internal static async Task<List<string>> CheckAsync(
        OctopushyDb db, Run? run, RunContext context, CancellationToken ct, IReadOnlyCollection<string>? neededNames = null)
    {
        var problems = Validate(context);
        if (neededNames is not null)
            problems.AddRange(neededNames.Where(n => !context.Secrets.ContainsKey(n)).Select(n => $"Missing credential role {n}; inspect and replan"));
        if (problems.Count > 0)
            return problems;
        // Pin every binding initially, but later steps need only their own credentials:
        // revoking a completed apply's privileged token must not prevent guest verification.
        var bindings = context.Secrets.Where(s => !context.SecretBindingsPinned || neededNames is null || neededNames.Contains(s.Key)).ToList();
        var keys = bindings.Select(s => s.Value).Distinct().ToArray();
        var entries = await db.StoreEntries.AsNoTracking().Where(e => keys.Contains(e.Key)).ToDictionaryAsync(e => e.Key, ct);
        var versions = await db.StoreVersions.AsNoTracking().Where(v => keys.Contains(v.Key)).ToListAsync(ct);
        var selected = new Dictionary<string, int>(StringComparer.Ordinal);
        var refused = context.Ansible is null && context.Stages is null ? [] : await ProxmoxKeysAsync(db, keys, ct);

        foreach (var (name, key) in bindings)
        {
            var role = RunContext.BareRole(name);
            // A guest's own key is the one thing under system/ a run may bind, and only its own guest's.
            var ownKey = role == SecretNames.AnsibleSshKey && run is not null && key == GuestAccessCredentials.Key(context.GuestResourceId ?? run.ResourceId)
                // And a container's own pin, which its create task gave.
                || role == SecretNames.AnsibleHostKey && run is not null && key == ContainerHostKeys.Key(context.GuestResourceId ?? run.ResourceId);
            if (key.StartsWith(VariableStore.SystemScope, StringComparison.Ordinal) && !ownKey)
            {
                problems.Add($"{key}: reserved system scope");
                continue;
            }

            if (role != SecretNames.AnsibleHostKey && refused.Contains(key))
            {
                problems.Add($"{key}: this is a credential of Proxmox or OpenTofu work, so a playbook cannot use it");
                continue;
            }

            if (!entries.TryGetValue(key, out var entry))
            {
                problems.Add(key.StartsWith(ContainerHostKeys.Prefix, StringComparison.Ordinal)
                    ? $"{key}: missing: the container's host keys have not been read from its create task yet. A provision run's verify step reads them; plan the guest"
                    : $"{key}: missing");
                continue;
            }

            if (RequiresSecret(name) && entry.Visibility != Visibilities.Secret)
            {
                problems.Add($"{key}: credential must be stored as secret, not plain");
                continue;
            }

            // A pin is public and goes to steps with no secrets, so a secret must not pose as one.
            if (role == SecretNames.AnsibleHostKey && entry.Visibility == Visibilities.Secret)
            {
                problems.Add($"{key}: a host key is public; store it as plain");
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
                version = context.RequestedSecretVersions.GetValueOrDefault(name, entry.CurrentVersion);
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
            if (run is not null)
                run.Context = context.Serialize();
        }

        return problems;
    }
}
