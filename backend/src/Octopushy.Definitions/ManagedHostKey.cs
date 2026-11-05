using System.Text.Json.Nodes;

namespace Octopushy.Definitions;

/// <summary>Where a host can receive a guest's cloud-init snippet: its sshd, and the key it is pinned to.</summary>
public sealed record SnippetHost(string Address, int Port, StoreReference HostKey);

/// <summary>Where the host key a run pins comes from. See <see cref="ManagedHostKey.TrustOf(LabRepository, GuestDefinition)"/>.</summary>
public enum HostKeyTrust
{
    /// <summary>Neither: a run that would log in is refused.</summary>
    None,
    /// <summary><c>access.hostKey</c>: a store entry an operator set.</summary>
    Declared,
    /// <summary>The key Octopushy generated for a VM it creates, and delivered.</summary>
    Managed,
    /// <summary>A container's: the fingerprints Proxmox logged when it created it. See <see cref="ContainerHostKey"/>.</summary>
    Container,
}

/// <summary>An installation's snippet uploads, from its <c>snippets</c> block.</summary>
public sealed record SnippetSettings(string Datastore, string User, IReadOnlyDictionary<string, SnippetHost> Hosts)
{
    public const string DefaultDatastore = "local";
    public const string DefaultUser = "octopushy-snippets";
}

/// <summary>
/// Which guests have an SSH host key of Octopushy's. Octopushy generates it, a provision run delivers it in a
/// cloud-init snippet, and configure runs pin it, so nobody reads a key off a console. That needs three things: a
/// guest Octopushy logs in to, a workflow whose machine stage installs the key (<c>hostKey: managed</c>), and a host
/// set up for snippet uploads. A guest that names <c>access.hostKey</c> keeps its own: that is how a machine
/// Octopushy did not create is reached.
/// </summary>
public static class ManagedHostKey
{
    /// <summary>The installation's snippet settings, or null when it has none.</summary>
    public static SnippetSettings? Settings(InstallationDefinition installation)
    {
        if (installation.Json["snippets"] is not JsonObject snippets)
            return null;
        var hosts = new Dictionary<string, SnippetHost>(StringComparer.Ordinal);
        foreach (var (id, node) in (snippets["hosts"] as JsonObject) ?? [])
        {
            if (node is not JsonObject host || host["hostKey"]?["store"]?.GetValue<string>() is not { } key)
                continue;
            var address = host["address"]?.GetValue<string>()
                ?? (installation.Hosts.TryGetValue(id, out var definition)
                    && Uri.TryCreate(ProvisionInputs.EndpointFor(installation, definition), UriKind.Absolute, out var endpoint) ? endpoint.Host : null);
            if (address is null)
                continue;
            hosts[id] = new SnippetHost(address, host["port"]?.GetValue<int>() ?? 22, new StoreReference(key, host["hostKey"]?["version"]?.GetValue<int>()));
        }

        return new SnippetSettings(
            snippets["datastore"]?.GetValue<string>() ?? SnippetSettings.DefaultDatastore,
            snippets["user"]?.GetValue<string>() ?? SnippetSettings.DefaultUser,
            hosts);
    }

    /// <summary>Whether the workflow makes a VM and its machine stage installs the key Octopushy gives it: <c>hostKey: managed</c>.</summary>
    public static bool Delivers(WorkflowDefinition? workflow) =>
        workflow?.Manages == WorkflowKinds.Vm && workflow.Machine?.ManagedHostKey == true;

    /// <summary>Whether the guest is a VM: only a VM gets a host key Octopushy generates.</summary>
    public static bool IsVm(GuestDefinition guest) => guest.Type == GuestTypes.Vm;

    /// <summary>
    /// Where a guest's host key pin comes from, chosen here once for every run that logs in, and never from a missing
    /// <c>access.hostKey</c> alone. In this order:
    /// <list type="number">
    /// <item><see cref="HostKeyTrust.Declared"/> when the guest names <c>access.hostKey</c>, which always wins;</item>
    /// <item><see cref="HostKeyTrust.Container"/> for a container, pinned to the fingerprints its create task logged;</item>
    /// <item><see cref="HostKeyTrust.Managed"/> for a VM whose workflow delivers Octopushy's key to a host set up for it;</item>
    /// <item><see cref="HostKeyTrust.None"/> otherwise, which no run may log in with.</item>
    /// </list>
    /// A container never takes a managed key, and a VM never a container's pin.
    /// </summary>
    public static HostKeyTrust TrustOf(LabRepository repository, GuestDefinition guest) =>
        TrustOf(repository.Installation(guest.InstallationAddress), guest);

    /// <inheritdoc cref="TrustOf(LabRepository, GuestDefinition)"/>
    public static HostKeyTrust TrustOf(InstallationDefinition? installation, GuestDefinition guest) =>
        guest.Json["access"]?["hostKey"] is not null ? HostKeyTrust.Declared
        : guest.Type == GuestTypes.Container ? HostKeyTrust.Container
        : Offered(installation, guest) ? HostKeyTrust.Managed
        : HostKeyTrust.None;

    /// <summary>Whether Octopushy could give this guest a key: its workflow delivers one, to a host set up for it.</summary>
    public static bool Offered(LabRepository repository, GuestDefinition guest) =>
        Offered(repository.Installation(guest.InstallationAddress), guest);

    /// <inheritdoc cref="Offered(LabRepository, GuestDefinition)"/>
    public static bool Offered(InstallationDefinition? installation, GuestDefinition guest) =>
        IsVm(guest) && Delivers(guest.Workflow) && installation is not null && Settings(installation)?.Hosts.ContainsKey(guest.Host) == true;

    /// <summary>
    /// A guest whose host key is Octopushy's: one Octopushy provisions and runs Ansible work on, from its workflow's
    /// setup stage or a service's stage, as for its login key in <see cref="ManagedAccess.Wanted"/>, where it is
    /// offered, unless it names <c>access.hostKey</c>, which always wins.
    /// </summary>
    /// <param name="keyIssued">
    /// Whether Octopushy already holds a host key for this guest. Once it does, the guest keeps it while it is offered,
    /// whatever happens to its Ansible work: dropping the snippet would replace the VM.
    /// </param>
    public static bool Wanted(LabRepository repository, GuestDefinition guest, bool keyIssued) =>
        guest.Json["access"]?["hostKey"] is null
        && !guest.Retired
        && (keyIssued || guest.Setup is not null || ManagedAccess.RunsPlaybooks(repository, guest))
        && Offered(repository, guest);
}
