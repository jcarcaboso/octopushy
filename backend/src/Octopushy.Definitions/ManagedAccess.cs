namespace Octopushy.Definitions;

/// <summary>Which guests Octopushy reaches with a key of its own, which it generates and keeps.</summary>
public static class ManagedAccess
{
    /// <summary>
    /// A guest Octopushy provisions that runs any Ansible work, from a setup recipe or a stage of one of its valid
    /// services alike, unless it names a key itself. The key follows who manages the machine, not what kind of
    /// work runs on it, and <c>access.sshKey</c> always wins.
    /// </summary>
    /// <param name="keyIssued">
    /// Whether Octopushy already holds a key for this guest. Once it does, the guest keeps wanting it until the guest is
    /// retired or names <c>access.sshKey</c>, whatever happens to its setup recipe or services. Taking the public key off
    /// a machine is never worth provisioning it again for, which reboots a VM and replaces a container, and a service
    /// broken in any way, even one that cannot be parsed, must not reach the guest's provisioning.
    /// </param>
    public static bool Wanted(LabRepository repository, GuestDefinition guest, bool keyIssued) =>
        guest.Json["access"]?["sshKey"] is null
        && (keyIssued || guest.SetupRecipe is not null || RunsPlaybooks(repository, guest));

    /// <summary>Whether a valid service on the guest has a stage that runs on it: a recipe with <c>operation: configure</c>.</summary>
    private static bool RunsPlaybooks(LabRepository repository, GuestDefinition guest) =>
        repository.Services.Any(s => s.GuestAddress == guest.Address && repository.IsValid(s.Address)
            && s.Stages.Any(stage => repository.Recipes.GetValueOrDefault(stage.Recipe)?.Configure is not null));
}
