using Microsoft.EntityFrameworkCore;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Git;

namespace Octopushy.Server.Uptime;

/// <summary>
/// One uptime check: what is checked, how, and what it says something about. See docs/uptime.md.
/// </summary>
/// <param name="Key">
/// Stable while what is checked stays the same: the kind, the subject and <paramref name="Url"/>. A new address, endpoint or
/// link is a new check, with its own history.
/// </param>
/// <param name="Kind"><c>proxmox-api</c>, <c>guest-ssh</c> or <c>service-http</c>.</param>
/// <param name="Installation">The installation it belongs to, <c>lab/installation</c>.</param>
/// <param name="Host">
/// The Proxmox host: the host whose API endpoint this is, or the guest's host. Null for an API endpoint several hosts share,
/// which answers for the installation, not for one host.
/// </param>
/// <param name="Resource">The guest or service address; null for a Proxmox API check.</param>
/// <param name="Url">What is checked, for people: <c>https://10.1.0.10:8006/</c>, <c>ssh://10.1.0.31:22</c>, or a service's link.</param>
public sealed record UptimeTarget(
    string Key,
    string Kind,
    string Installation,
    string? Host,
    string? Resource,
    string Url,
    string Protocol,
    string HostName,
    int Port,
    string? ServerName,
    string? Path,
    bool Tls)
{
    public const string ProxmoxApi = "proxmox-api";
    public const string GuestSsh = "guest-ssh";
    public const string ServiceHttp = "service-http";

    /// <summary>The most checks kept, so a repository cannot make the agents probe without bound.</summary>
    public const int MaxTargets = 1000;

    /// <summary>
    /// What to check, from the head revision of the current repository source when it is valid, for installations an
    /// operator trusted in the mode they are in now: their hosts' API endpoints that the trust covers, the SSH address of
    /// each active guest, and the links of its services. Nothing from an untrusted installation is checked.
    /// </summary>
    public static async Task<IReadOnlyList<UptimeTarget>> ForAsync(OctopushyDb db, RevisionCache revisions, CancellationToken cancellationToken)
    {
        var head = await RepositoryTrust.HeadRevisions(db).AsNoTracking().Where(r => r.RepositoryValid)
            .OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(cancellationToken);
        if (head is null)
            return [];
        var repository = await revisions.GetAsync(head, cancellationToken);
        var trusts = await db.InstallationTrusts.AsNoTracking().ToDictionaryAsync(t => t.Address, cancellationToken);
        return From(repository, trusts);
    }

    /// <inheritdoc cref="ForAsync"/>
    public static IReadOnlyList<UptimeTarget> From(LabRepository repository, IReadOnlyDictionary<string, InstallationTrust> trusts)
    {
        var targets = new List<UptimeTarget>();
        foreach (var installation in repository.Installations.OrderBy(i => i.Address, StringComparer.Ordinal))
        {
            if (!trusts.TryGetValue(installation.Address, out var trust) || trust.ObservedOnly == installation.Managed)
                continue;
            var endpoints = installation.Hosts.Values
                .Select(h => (Host: h.Id, Endpoint: ProvisionInputs.EndpointFor(installation, h)))
                .Where(h => trust.Endpoints.Contains(h.Endpoint))
                .GroupBy(h => h.Endpoint, StringComparer.Ordinal);
            foreach (var endpoint in endpoints.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (!Uri.TryCreate(endpoint.Key.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0)
                    continue;
                var host = endpoint.Count() == 1 ? endpoint.First().Host : null;
                var url = $"https://{uri.Authority}/";
                targets.Add(new($"{ProxmoxApi}|{installation.Address}/{host}|{url}", ProxmoxApi, installation.Address, host, null, url,
                    "tls", uri.DnsSafeHost, uri.Port, uri.DnsSafeHost, null, true));
            }

            foreach (var guest in repository.Guests.Where(g => g.InstallationAddress == installation.Address && !g.Retired)
                         .OrderBy(g => g.Address, StringComparer.Ordinal))
            {
                if (ConfigureInputs.AddressOf(guest.Json)?.Trim().TrimStart('[').TrimEnd(']') is { Length: > 0 } address)
                {
                    var port = ConfigureInputs.PortOf(guest.Json);
                    var url = $"ssh://{(address.Contains(':') ? $"[{address}]" : address)}:{port}";
                    targets.Add(new($"{GuestSsh}|{guest.Address}|{url}", GuestSsh, installation.Address, guest.Host, guest.Address, url,
                        "ssh", address, port, null, null, false));
                }

                foreach (var service in repository.Services.Where(s => s.GuestAddress == guest.Address).OrderBy(s => s.Address, StringComparer.Ordinal))
                {
                    foreach (var link in service.Links)
                    {
                        var uri = new Uri(link);
                        targets.Add(new($"{ServiceHttp}|{service.Address}|{link}", ServiceHttp, installation.Address, guest.Host, service.Address,
                            link, "http", uri.DnsSafeHost, uri.Port, uri.DnsSafeHost, uri.PathAndQuery, uri.Scheme == Uri.UriSchemeHttps));
                    }
                }
            }
        }

        return targets.DistinctBy(t => t.Key).Take(MaxTargets).ToList();
    }
}
