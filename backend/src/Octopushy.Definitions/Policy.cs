using System.Text.Json.Nodes;

namespace Octopushy.Definitions;

/// <summary>A resolved setting and the place it was set, shown so the user can find it.</summary>
public sealed record ResolvedSetting<T>(T Value, string SetBy);

/// <summary>
/// The approval and destructive settings that apply to one resource.
/// <paramref name="DestructiveExplicit"/> says someone set destructive, rather than it following approval.
/// </summary>
public sealed record EffectivePolicy(ResolvedSetting<string> Approval, ResolvedSetting<string> Destructive, bool DestructiveExplicit)
{
    public bool ApprovalRequired => Approval.Value == "manual";

    public bool DestructiveApprovalRequired => Destructive.Value == "manual";
}

/// <summary>
/// Settings flow down the tree (octopushy.yaml defaults, lab, installation, guest, service) and
/// the nearest one wins. An unset destructive setting takes the resource's approval setting.
/// </summary>
public static class PolicyResolver
{
    public const string Automatic = "automatic";
    public const string Manual = "manual";

    public static EffectivePolicy For(LabRepository repository, GuestDefinition guest) =>
        Resolve(Chain(repository, guest.Lab, repository.Installation(guest.InstallationAddress))
            .Append((guest.Address, guest.Policy)));

    /// <summary>
    /// The policy for one action of the guest's setup stage. An action's own approval wins over every other approval
    /// setting, whatever starts the action: the guest's <c>policy.actions</c> first, then the workflow's default for the
    /// action. Destructive is not touched: unset, it still follows the guest's own approval, so a destructive action does
    /// not run unattended because of this.
    /// </summary>
    public static EffectivePolicy ForAction(LabRepository repository, GuestDefinition guest, string action)
    {
        var policy = For(repository, guest);
        return ActionApproval(guest, action) is { } approval ? policy with { Approval = approval } : policy;
    }

    /// <summary>The approval one action takes in place of the guest's, and who set it, if anyone did.</summary>
    public static ResolvedSetting<string>? ActionApproval(GuestDefinition guest, string action) =>
        GuestActionApprovals(guest).TryGetValue(action, out var own)
            ? new ResolvedSetting<string>(own, $"{guest.Address}, action {action}")
            : guest.Setup?.Configure?.Actions.GetValueOrDefault(action)?.Approval is { } given
                ? new ResolvedSetting<string>(given, $"workflow {guest.Workflow!.Label}, action {action}")
                : null;

    /// <summary>The actions of the guest's setup stage that take their own approval, in the workflow's order.</summary>
    public static IReadOnlyList<(string Action, ResolvedSetting<string> Approval)> ActionApprovals(GuestDefinition guest) =>
        (guest.Setup?.Configure?.Actions.Keys ?? [])
            .Select(action => (action, ActionApproval(guest, action)))
            .Where(a => a.Item2 is not null)
            .Select(a => (a.action, a.Item2!))
            .ToList();

    /// <summary>
    /// Every action the guest itself sets an approval for, as written. This also reads definitions that failed their
    /// schema, which report their own error, so what is malformed is left out.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GuestActionApprovals(GuestDefinition guest) =>
        ((guest.Json["policy"] as JsonObject)?["actions"] as JsonObject)?
            .Where(p => (p.Value as JsonObject)?["approval"] is JsonValue value && value.TryGetValue<string>(out _))
            .ToDictionary(p => p.Key, p => p.Value!["approval"]!.GetValue<string>(), StringComparer.Ordinal)
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>A service adds only its own override to what its guest resolves to.</summary>
    public static EffectivePolicy For(LabRepository repository, GuestDefinition guest, ServiceDefinition service) =>
        Resolve(Chain(repository, guest.Lab, repository.Installation(guest.InstallationAddress))
            .Append((guest.Address, guest.Policy))
            .Append((service.Address, service.Policy)));

    public static EffectivePolicy For(LabRepository repository, InstallationDefinition installation) =>
        Resolve(Chain(repository, installation.Lab, installation));

    /// <summary>The labels an agent needs to act on the guest; empty means any agent.</summary>
    public static ResolvedSetting<IReadOnlyList<string>> AgentLabels(LabRepository repository, GuestDefinition guest) =>
        ResolveLabels(LabelChain(repository, guest.Lab, repository.Installation(guest.InstallationAddress))
            .Append((guest.Address, guest.AgentLabels)));

    public static ResolvedSetting<IReadOnlyList<string>> AgentLabels(LabRepository repository, GuestDefinition guest, ServiceDefinition service) =>
        ResolveLabels(LabelChain(repository, guest.Lab, repository.Installation(guest.InstallationAddress))
            .Append((guest.Address, guest.AgentLabels))
            .Append((service.Address, service.AgentLabels)));

    public static ResolvedSetting<IReadOnlyList<string>> AgentLabels(LabRepository repository, InstallationDefinition installation) =>
        ResolveLabels(LabelChain(repository, installation.Lab, installation));

    private static IEnumerable<(string SetBy, PolicySettings? Policy)> Chain(
        LabRepository repository,
        string lab,
        InstallationDefinition? installation)
    {
        yield return ("octopushy.yaml", repository.Settings?.DefaultPolicy);
        yield return (lab, repository.Lab(lab)?.Policy);
        if (installation is not null)
        {
            yield return (installation.Address, installation.Policy);
        }
    }

    private static IEnumerable<(string SetBy, IReadOnlyList<string>? Labels)> LabelChain(
        LabRepository repository,
        string lab,
        InstallationDefinition? installation)
    {
        yield return ("octopushy.yaml", repository.Settings?.DefaultAgentLabels);
        yield return (lab, repository.Lab(lab)?.AgentLabels);
        if (installation is not null)
        {
            yield return (installation.Address, installation.AgentLabels);
        }
    }

    private static EffectivePolicy Resolve(IEnumerable<(string SetBy, PolicySettings? Policy)> chain)
    {
        var approval = new ResolvedSetting<string>(Automatic, "default");
        ResolvedSetting<string>? destructive = null;
        foreach (var (setBy, policy) in chain)
        {
            if (policy?.Approval is { } a)
            {
                approval = new ResolvedSetting<string>(a, setBy);
            }

            if (policy?.Destructive is { } d)
            {
                destructive = new ResolvedSetting<string>(d, setBy);
            }
        }

        return new EffectivePolicy(
            approval,
            destructive ?? new ResolvedSetting<string>(approval.Value, $"{approval.SetBy}, following approval"),
            destructive is not null);
    }

    private static ResolvedSetting<IReadOnlyList<string>> ResolveLabels(IEnumerable<(string SetBy, IReadOnlyList<string>? Labels)> chain)
    {
        var result = new ResolvedSetting<IReadOnlyList<string>>([], "default");
        foreach (var (setBy, labels) in chain)
        {
            if (labels is not null)
            {
                result = new ResolvedSetting<IReadOnlyList<string>>(labels, setBy);
            }
        }

        return result;
    }
}
