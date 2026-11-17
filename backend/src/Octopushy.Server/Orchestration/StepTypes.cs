namespace Octopushy.Server.Orchestration;

/// <summary>Which secrets a step's assignment carries, as <see cref="AssignmentBuilder.SecretsFor(string, RunContext)"/> picks them.</summary>
public enum StepSecrets
{
    /// <summary>A step a person or the engine settles: it runs on no agent and carries nothing.</summary>
    None,

    /// <summary>The step's Proxmox token, and the installation's CA when the run binds one.</summary>
    Proxmox,

    /// <summary>
    /// The OpenTofu environment's secrets, the Proxmox token among them, except the snippet roles in
    /// <see cref="StepType.SnippetRoles"/> that this step does not hold.
    /// </summary>
    OpenTofu,

    /// <summary>Only the host key pin, which is public: the step runs repository code nobody approved yet.</summary>
    AnsiblePin,

    /// <summary>Every role the run binds.</summary>
    Ansible,
}

/// <summary>
/// What a step name means to an agent: the action it runs as, whether that changes anything, the Proxmox token it holds and
/// the secrets it carries. <paramref name="Action"/> is null for a step that runs on no agent.
/// </summary>
/// <param name="SnippetRoles">
/// For an OpenTofu step, the snippet upload roles it holds: the plan writes the guest's private host key into the snippet,
/// and the apply alone uploads it, with the upload key and the host's own key to pin.
/// </param>
public sealed record StepType(string Name, StepAction? Action, string Token, StepSecrets Secrets, IReadOnlyList<string>? SnippetRoles = null);

/// <summary>Every step name, and what it runs as. One entry per name in <see cref="StepNames"/>.</summary>
public static class StepTypes
{
    private static readonly StepAction OpenTofuPreview = new("opentofu", "preview", false);
    private static readonly StepAction OpenTofuApply = new("opentofu", "apply", true);
    private static readonly StepAction SetProtection = new("proxmox", "set-protection", true);
    private static readonly StepAction AnsibleChange = new("ansible", "run", true);
    private static readonly StepAction AnsibleCheck = new("ansible", "run", false);

    private const string ReadOnly = SecretNames.ProxmoxReadOnlyToken;
    private const string Privileged = SecretNames.ProxmoxPrivilegedToken;

    /// <summary>The snippet roles an OpenTofu step gets only when its entry names them.</summary>
    internal static readonly IReadOnlyList<string> HeldSnippetRoles = [SecretNames.GuestHostKey, SecretNames.SnippetsKey];

    private static readonly StepType[] Entries =
    [
        new(StepNames.Plan, OpenTofuPreview, ReadOnly, StepSecrets.OpenTofu, [SecretNames.GuestHostKey]),
        new(StepNames.Approval, null, ReadOnly, StepSecrets.None),
        new(StepNames.Unprotect, SetProtection, Privileged, StepSecrets.Proxmox),
        new(StepNames.Apply, OpenTofuApply, Privileged, StepSecrets.OpenTofu, [SecretNames.SnippetsKey, SecretNames.SnippetsHostKey]),
        new(StepNames.Reprotect, SetProtection, Privileged, StepSecrets.Proxmox),
        new(StepNames.Protect, SetProtection, Privileged, StepSecrets.Proxmox),
        new(StepNames.Verify, new("proxmox", "verify-guest", false), ReadOnly, StepSecrets.Proxmox),
        new(StepNames.VerifyAbsent, new("proxmox", "verify-absent", false), ReadOnly, StepSecrets.Proxmox),
        new(StepNames.Observe, new("proxmox", "observe", false), ReadOnly, StepSecrets.Proxmox),
        new(StepNames.Preview, new("ansible", "preview", false), ReadOnly, StepSecrets.AnsiblePin),
        new(StepNames.Playbook, AnsibleChange, ReadOnly, StepSecrets.Ansible),
        new(StepNames.OutcomeApproval, null, ReadOnly, StepSecrets.None),
        new(StepNames.PlaybookApproved, AnsibleChange, ReadOnly, StepSecrets.Ansible),
        new(StepNames.VerifyCurrent, AnsibleCheck, ReadOnly, StepSecrets.Ansible),
        new(StepNames.AuthorizeKey, AnsibleChange, ReadOnly, StepSecrets.Ansible),
        new(StepNames.VerifyKey, AnsibleCheck, ReadOnly, StepSecrets.Ansible),
        new(StepNames.PromoteKey, null, ReadOnly, StepSecrets.None),
        new(StepNames.ApproveRemoval, null, ReadOnly, StepSecrets.None),
        new(StepNames.RemoveKey, AnsibleChange, ReadOnly, StepSecrets.Ansible),
        new(StepNames.VerifyRemoval, AnsibleCheck, ReadOnly, StepSecrets.Ansible),
        new(StepNames.ConfirmRefused, AnsibleCheck, ReadOnly, StepSecrets.Ansible),
    ];

    private static readonly Dictionary<string, StepType> ByName = Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);

    public static IReadOnlyCollection<StepType> All => Entries;

    /// <summary>The entry for <paramref name="name"/>, matched exactly; null for a name no step has.</summary>
    public static StepType? Find(string? name) => name is not null && ByName.TryGetValue(name, out var type) ? type : null;
}
