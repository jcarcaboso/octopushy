namespace Octopushy.Definitions;

/// <summary>Which guests Octopushy reaches with a key of its own, which it generates and keeps.</summary>
public static class ManagedAccess
{
    /// <summary>
    /// A guest Octopushy configures, unless it names a key for a machine Octopushy did not create.
    /// Anything else that logs in to a guest, such as services, belongs here too, so that one
    /// rule decides who gets a key.
    /// </summary>
    public static bool Wanted(GuestDefinition guest) =>
        guest.SetupRecipe is not null && guest.Json["access"]?["sshKey"] is null;
}
