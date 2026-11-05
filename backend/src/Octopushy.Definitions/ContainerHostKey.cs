namespace Octopushy.Definitions;

/// <summary>
/// Which container guests Octopushy pins the SSH host key of by itself. Proxmox generates a container's host keys on the
/// host when it creates the container, before it first starts, and logs each key's SHA-256 fingerprint in the create task.
/// The verify step reads them there, through the API the installation is trusted for, and Ansible work is then pinned to
/// them. No person reads a key off a console. See docs/guest-configuration.md.
/// </summary>
public static class ContainerHostKey
{
    /// <summary>
    /// A container that names no <c>access.hostKey</c> and runs Ansible work, from its workflow's setup stage or a stage of one
    /// of its valid services alike. <c>access.hostKey</c> always wins.
    /// </summary>
    public static bool Wanted(LabRepository repository, GuestDefinition guest) =>
        Pinned(guest) && (guest.Setup is not null || ManagedAccess.RunsPlaybooks(repository, guest));

    /// <summary>
    /// A container with no <c>access.hostKey</c>: its Ansible work is pinned to what its create task logged. The decision is
    /// <see cref="ManagedHostKey.TrustOf(InstallationDefinition?, GuestDefinition)"/>'s, which needs no installation for it.
    /// </summary>
    public static bool Pinned(GuestDefinition guest) =>
        ManagedHostKey.TrustOf((InstallationDefinition?)null, guest) == HostKeyTrust.Container;
}
