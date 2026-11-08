using Microsoft.EntityFrameworkCore;
using Octopushy.Server.Agents;
using Octopushy.Server.Data;
using Octopushy.Server.Git;

namespace Octopushy.Server.Orchestration;

/// <summary>
/// What a step connects to. <paramref name="Modeled"/> is false where nothing is derived, as for a pipeline's OpenTofu
/// stage, whose providers are the workflow's own: such a step goes to any agent, unchecked. An empty
/// <paramref name="Targets"/> with <paramref name="Modeled"/> is a step that connects to nothing, as a configure
/// preview.
/// </summary>
public sealed record NetworkNeeds(bool Modeled, IReadOnlyList<NetworkTarget> Targets)
{
    public static readonly NetworkNeeds None = new(true, []);
    public static readonly NetworkNeeds NotModeled = new(false, []);

    /// <summary>How the step's network access is covered, for people.</summary>
    public string Coverage => !Modeled ? "Network access not modeled: this step goes to any agent, and its reachability is not checked."
        : Targets.Count == 0 ? "No network access: this step connects to nothing."
        : $"Needs fresh reachability of {string.Join(", ", Targets.Select(t => t.Label))}.";
}

/// <summary>
/// The connections a step makes, derived from what it runs and the credentials it is released, so routing and
/// release cannot drift apart: <see cref="AssignmentBuilder.ActionFor"/>, <see cref="AssignmentBuilder.SecretsFor(string, RunContext)"/>
/// and <see cref="RunContext.ForStep"/>. Never from what a context merely holds: every context has a Proxmox
/// connection, and only steps that use it need it.
/// </summary>
public static class NetworkRequirements
{
    private static readonly string[] ProxyNames = ["HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY"];

    /// <param name="context">The step's own view, <see cref="RunContext.ForStep"/>.</param>
    public static async Task<NetworkNeeds> ForAsync(OctopushyDb db, string stepName, RunContext context, CancellationToken cancellationToken)
    {
        var action = AssignmentBuilder.ActionFor(stepName);
        switch (action.Executor)
        {
            case "proxmox":
                // observe, verify-guest (a VM's address comes from the guest agent through the API, a container's host
                // keys from its create task), verify-absent, set-protection: all in the agent process.
                return new(true, [await ApiAsync(db, context, NetworkTarget.AgentContext, null, null, cancellationToken)]);
            case "opentofu":
                // A pipeline's OpenTofu stage gets no endpoint and no provider credential from the server: nothing to derive.
                if (context.Stage is not null || context.OpenTofu is not { } tofu)
                    return NetworkNeeds.NotModeled;
                var proxy = ProxyIn(tofu.Env.Keys.Concat(tofu.SecretEnv.Keys));
                var targets = new List<NetworkTarget> { await ApiAsync(db, context, NetworkTarget.ToolContext, tofu.Image, proxy, cancellationToken) };
                // The upload happens on apply only, exactly when the apply is released the node's host key it is pinned to.
                if (context.Snippets is { } snippets && AssignmentBuilder.SecretsFor(stepName, context).Contains(SecretNames.SnippetsHostKey))
                    targets.Add(Ssh("snippets-ssh", snippets.Address, snippets.Port, tofu.Image, proxy));
                return new(true, targets);
            case "ansible":
                // A preview makes no contact with the guest.
                if (stepName == StepNames.Preview)
                    return NetworkNeeds.None;
                var ansible = context.Ansible ?? throw new InvalidOperationException("A configure step has no Ansible context.");
                return new(true, [Ssh("guest-ssh", ansible.Address, ansible.Port, ansible.Image, null)]);
            default:
                return NetworkNeeds.NotModeled;
        }
    }

    /// <summary>How an agent step's network access is checked, for people; null for a step no agent runs.</summary>
    public static async Task<string?> CoverageAsync(OctopushyDb db, Step step, RunContext run, CancellationToken cancellationToken) =>
        step.Kind == StepKinds.Agent ? (await ForAsync(db, step.Name, run.ForStep(step), cancellationToken)).Coverage : null;

    /// <summary>
    /// The API endpoints of installations, as their latest observation reaches them, for background refresh: only
    /// observations of an eligible revision that dispatch would still send.
    /// </summary>
    public static async Task<IReadOnlyList<NetworkTarget>> InstallationTargetsAsync(OctopushyDb db, CancellationToken cancellationToken)
    {
        var eligible = RepositoryTrust.EligibleRevisions(db);
        var latest = await db.Attempts.AsNoTracking()
            .Where(a => a.StepId == null && a.ResourceId != null && a.Context != null && eligible.Any(r => r.Id == a.RevisionId))
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new { a.ResourceId, a.RevisionId, a.Context, Address = db.Resources.Where(r => r.Id == a.ResourceId).Select(r => r.Address).FirstOrDefault() })
            .Take(500)
            .ToListAsync(cancellationToken);
        var targets = new List<NetworkTarget>();
        foreach (var observation in latest.DistinctBy(a => a.ResourceId).Take(50))
        {
            var context = RunContext.Parse(observation.Context!);
            if (await RepositoryTrust.BlockedReasonAsync(db, observation.RevisionId, context.Commit, context, cancellationToken) is not null
                || await SafetyBoundary.ObservationRefusalAsync(db, observation.Address, context, cancellationToken) is not null)
                continue;
            targets.AddRange((await ForAsync(db, StepNames.Observe, context, cancellationToken)).Targets);
        }

        return targets.DistinctBy(t => t.KeyFor(null)).ToList();
    }

    /// <summary>The Proxmox API, judged as the connection is configured: a CA it is released, else verifyTls.</summary>
    private static async Task<NetworkTarget> ApiAsync(OctopushyDb db, RunContext context, string from, string? image, string? proxy,
        CancellationToken cancellationToken)
    {
        var proxmox = context.Proxmox;
        var ca = context.Secrets.TryGetValue(SecretNames.ProxmoxCa, out var caKey) ? caKey : null;
        var trust = ca is not null ? "ca" : proxmox.VerifyTls ? "system" : "insecure";
        int? caVersion = null;
        if (ca is not null)
            caVersion = context.SecretVersions.TryGetValue(SecretNames.ProxmoxCa, out var pinned) ? pinned
                : context.RequestedSecretVersions.TryGetValue(SecretNames.ProxmoxCa, out var requested) ? requested
                : await db.StoreEntries.AsNoTracking().Where(e => e.Key == ca).Select(e => (int?)e.CurrentVersion).FirstOrDefaultAsync(cancellationToken);
        if (!Uri.TryCreate(proxmox.Endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.DnsSafeHost.Length == 0)
            return new("proxmox-api", "tls", proxmox.Endpoint, 443, null, trust, ca, caVersion, from, image,
                "only an https:// endpoint is probed");
        var host = uri.DnsSafeHost;
        return new("proxmox-api", "tls", host, uri.Port, host, trust, ca, caVersion, from, image, proxy);
    }

    private static NetworkTarget Ssh(string kind, string address, int port, string image, string? proxy) =>
        new(kind, "ssh", address.Trim().TrimStart('[').TrimEnd(']'), port, null, "none", null, null, NetworkTarget.ToolContext, image, proxy);

    /// <summary>A proxy the step's own environment sets, which probes would not go through.</summary>
    private static string? ProxyIn(IEnumerable<string> names) =>
        names.FirstOrDefault(n => ProxyNames.Contains(n, StringComparer.OrdinalIgnoreCase) || n.Contains("SOCKS", StringComparison.OrdinalIgnoreCase)) is { } name
            ? $"the step's environment sets {name}, and probes do not go through proxies"
            : null;
}
