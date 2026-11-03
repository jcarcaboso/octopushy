namespace Octopushy.Definitions;

/// <summary>Which guests Octopushy reaches with a key of its own, which it generates and keeps.</summary>
public static class ManagedAccess
{
    /// <summary>
    /// A guest Octopushy provisions that runs any Ansible work, from a setup recipe or a stage of one of its
    /// services alike, unless it names a key itself. The key follows who manages the machine, not what kind of
    /// work runs on it, and <c>access.sshKey</c> always wins. A managed worker is configured by its image.
    /// </summary>
    /// <param name="keyIssued">
    /// Whether Octopushy already holds a key for this guest. An invalid service counts only then, so an invalid
    /// pipeline neither gives its guest a key nor takes one away: either would change the guest's provisioning,
    /// which reboots a VM and replaces a container, before anything about the service is valid or approved.
    /// </param>
    public static bool Wanted(LabRepository repository, GuestDefinition guest, bool keyIssued) =>
        guest.Json["access"]?["sshKey"] is null
        && guest.ManagedWorker is null
        && (guest.SetupRecipe is not null
            || ServicesRunningPlaybooks(repository, guest).Any(s => repository.IsValid(s.Address) || keyIssued));

    /// <summary>The guest's services with a stage that runs on it: a recipe with <c>operation: configure</c>, valid or not.</summary>
    private static IEnumerable<ServiceDefinition> ServicesRunningPlaybooks(LabRepository repository, GuestDefinition guest) =>
        repository.Services.Where(s => s.GuestAddress == guest.Address
            && s.Stages.Any(stage => repository.Recipes.GetValueOrDefault(stage.Recipe)?.Configure is not null));
}
