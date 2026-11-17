using System.Reflection;
using System.Text;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Server;

/// <summary>
/// What every step name runs as, which token it holds and which secrets it carries, for each shape of run context the
/// factory makes. The table was recorded from the code before the step-type registry, so a change to it is a change of
/// behaviour, and the <c>mutating</c> column is what an attempt and the agent's cancel follow. Every name in
/// <see cref="StepNames"/> is listed by reflection, so a new one cannot be missed.
/// </summary>
public class StepTypeCharacterizationTests
{
    private const string Endpoint = "https://10.1.0.10:8006/";

    /// <summary>Names the code does not know, as a typo or another case would reach it.</summary>
    private static readonly string?[] Unknown = ["no-such-step", "", "Plan", "APPLY", " verify", null];

    private static IEnumerable<string?> Names() =>
        typeof(StepNames).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string?)f.GetRawConstantValue())
            .Order(StringComparer.Ordinal)
            .Concat(Unknown);

    private static readonly ProxmoxContext Proxmox = new(Endpoint, false, "proxmox/home/ca", "korriban", 310, "krb-app-01", "10.1.0.31", "qemu");

    private static Dictionary<string, string> Split(bool ca = true)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SecretNames.ProxmoxReadOnlyToken] = "proxmox/home/read",
            [SecretNames.ProxmoxPrivilegedToken] = "proxmox/home/write",
        };
        if (ca)
            secrets[SecretNames.ProxmoxCa] = "proxmox/home/ca";
        return secrets;
    }

    private static Dictionary<string, string> Shared(bool ca = true)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal) { [SecretNames.ProxmoxSharedToken] = "proxmox/home/token" };
        if (ca)
            secrets[SecretNames.ProxmoxCa] = "proxmox/home/ca";
        return secrets;
    }

    private static OpenTofuContext Tofu(bool snippets = false, TemplateImage? image = null)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["PROXMOX_VE_ENDPOINT"] = Endpoint, ["PROXMOX_VE_INSECURE"] = "true" };
        var secretEnv = new Dictionary<string, string>(StringComparer.Ordinal) { ["PROXMOX_VE_API_TOKEN"] = SecretNames.ProxmoxToken };
        if (snippets)
        {
            env[SecretNames.SnippetsUserEnv] = "octopushy";
            secretEnv[SecretNames.GuestHostKeyEnv] = SecretNames.GuestHostKey;
            secretEnv[SecretNames.SnippetsKeyEnv] = SecretNames.SnippetsKey;
        }
        return new OpenTofuContext("home.korriban.krb-app-01", "tofu", "opentofu:1", [], env, secretEnv, image);
    }

    private static Dictionary<string, string> WithSnippets(Dictionary<string, string> secrets)
    {
        secrets[SecretNames.GuestHostKey] = "system/guest-host-key/x";
        secrets[SecretNames.SnippetsKey] = "system/installation-snippets/home";
        secrets[SecretNames.SnippetsHostKey] = "proxmox/home/korriban-host-key";
        return secrets;
    }

    private static AnsibleContext Ansible(string action = "setup", bool destructive = false) =>
        new("app@1.0", action, "ansible", "setup.yml", null, false, destructive, null, "ansible:1", "krb-app-01", "guests/krb-app-01",
            "10.1.0.31", 22, "ops", [], ["db_password"], ["tls_key"], [], null);

    private static Dictionary<string, string> ConfigureSecrets(bool hostKey = true)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SecretNames.AnsibleSshKey] = "system/guest-access/x",
            [SecretNames.AnsibleVar("db_password")] = "apps/db",
            [SecretNames.AnsibleFile("tls_key")] = "apps/tls",
        };
        if (hostKey)
            secrets[SecretNames.AnsibleHostKey] = "guests/krb-app-01/host-key";
        return secrets;
    }

    private static RunContext Pipeline(bool managedHostKey)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RunContext.StageRole("deploy", SecretNames.AnsibleSshKey)] = "system/guest-access/x",
            [RunContext.StageRole("deploy", SecretNames.AnsibleVar("db_password"))] = "apps/db",
        };
        if (!managedHostKey)
            secrets[RunContext.StageRole("deploy", SecretNames.AnsibleHostKey)] = "guests/krb-app-01/host-key";
        return new RunContext
        {
            Address = "home/korriban/krb-app-01/app",
            Proxmox = Proxmox with { CaCertificateKey = null, ExpectIpv4 = null, Kind = null },
            ManagedHostKey = managedHostKey,
            Secrets = secrets,
            Stages =
            [
                new StageContext("dns", "app@1.0", new OpenTofuContext("home.korriban.krb-app-01.app.dns", "dns", "opentofu:1", [], [], []), null),
                new StageContext("deploy", "app@1.0", null, Ansible("deploy")),
            ],
        };
    }

    /// <summary>Each shape of context a run, or an observation, is built with, named by what made it.</summary>
    private static IEnumerable<(string Shape, RunContext Context)> Shapes()
    {
        var guest = new RunContext { Address = "home/korriban/krb-app-01", Proxmox = Proxmox };
        yield return ("provision split", guest with { OpenTofu = Tofu(), Secrets = Split() });
        yield return ("provision split without ca", guest with { OpenTofu = Tofu(), Secrets = Split(ca: false) });
        yield return ("provision shared", guest with { OpenTofu = Tofu(), Secrets = Shared() });
        yield return ("provision split snippets", guest with
        {
            OpenTofu = Tofu(snippets: true), Secrets = WithSnippets(Split()),
            Snippets = new SnippetsContext("home", "octopushy", "10.1.0.10", 22),
        });
        yield return ("provision shared snippets", guest with
        {
            OpenTofu = Tofu(snippets: true), Secrets = WithSnippets(Shared()),
            Snippets = new SnippetsContext("home", "octopushy", "10.1.0.10", 22),
        });
        // The snippet environment without the upload: a run the bindings check refuses, kept to pin which step gets which.
        yield return ("provision snippet env without upload", guest with { OpenTofu = Tofu(snippets: true), Secrets = WithSnippets(Split()) });
        yield return ("provision built template", guest with
        {
            OpenTofu = Tofu(image: new TemplateImage("0123abcd", 1024, "debian-13.qcow2")), Secrets = Split(),
        });
        yield return ("retire split", guest with { OpenTofu = Tofu(), Secrets = Split(), DefinitionRemovedIn = "abc123" });
        yield return ("retire shared", guest with { OpenTofu = Tofu(), Secrets = Shared() });
        yield return ("attach split", guest with { Secrets = Split() });
        yield return ("attach shared without ca", guest with { Secrets = Shared(ca: false) });
        var configure = guest with { Proxmox = Proxmox with { CaCertificateKey = null, ExpectIpv4 = null, Kind = null } };
        yield return ("configure", configure with { Ansible = Ansible(), Secrets = ConfigureSecrets() });
        yield return ("configure destructive", configure with { Ansible = Ansible("reset", destructive: true), Secrets = ConfigureSecrets() });
        yield return ("configure managed host key", configure with { Ansible = Ansible(), Secrets = ConfigureSecrets(hostKey: false), ManagedHostKey = true });
        yield return ("rotate-access", configure with
        {
            Ansible = Ansible(Operations.RotateAccess),
            Secrets = new() { [SecretNames.AnsibleHostKey] = "guests/krb-app-01/host-key" },
            AccessRotation = new AccessRotationContext(1, 2, [1]),
        });
        yield return ("rotate-access managed host key", configure with
        {
            Ansible = Ansible(Operations.RotateAccess), ManagedHostKey = true, AccessRotation = new AccessRotationContext(1, 2, [1]),
        });
        yield return ("pipeline", Pipeline(managedHostKey: false));
        yield return ("pipeline managed host key", Pipeline(managedHostKey: true));
        foreach (var managed in new[] { false, true })
        foreach (var stage in new[] { "dns", "deploy" })
            yield return ($"pipeline{(managed ? " managed host key" : "")} stage {stage}", Pipeline(managed).ForStage(stage));
        var installation = new RunContext { Address = "home", Proxmox = new ProxmoxContext(Endpoint, false, "proxmox/home/ca", null, null, null, null) };
        yield return ("observation split", installation with { Secrets = new() { [SecretNames.ProxmoxReadOnlyToken] = "proxmox/home/read", [SecretNames.ProxmoxCa] = "proxmox/home/ca" } });
        yield return ("observation shared without ca", installation with { Secrets = new() { [SecretNames.ProxmoxSharedToken] = "proxmox/home/token" } });
        yield return ("empty", new RunContext { Address = "home", Proxmox = installation.Proxmox });
    }

    private static readonly MethodInfo ContextToken = typeof(AssignmentBuilder).GetMethod(
        "TokenFor", BindingFlags.NonPublic | BindingFlags.Static, [typeof(string), typeof(RunContext)])
        ?? throw new InvalidOperationException("AssignmentBuilder has no TokenFor(string, RunContext).");

    private static string Show(string? name) => name is null ? "<null>" : $"'{name}'";

    private static string Outcome(Func<object> act)
    {
        try
        {
            return act() switch
            {
                StepAction a => $"{a.Executor}/{a.Action} mutating={a.Mutating}",
                IEnumerable<string> names => $"[{string.Join(", ", names)}]",
                var other => other.ToString()!,
            };
        }
        catch (TargetInvocationException e) when (e.InnerException is { } inner)
        {
            return $"throws {inner.GetType().Name}: {inner.Message}";
        }
        catch (Exception e)
        {
            return $"throws {e.GetType().Name}: {e.Message}";
        }
    }

    internal static string Table()
    {
        var table = new StringBuilder();
        foreach (var name in Names())
        {
            table.Append($"{Show(name)} action: {Outcome(() => AssignmentBuilder.ActionFor(name!))}\n");
            table.Append($"{Show(name)} token: {Outcome(() => AssignmentBuilder.TokenFor(name!))}\n");
        }
        foreach (var (shape, context) in Shapes())
        foreach (var name in Names())
        {
            table.Append($"{shape} {Show(name)} token: {Outcome(() => ContextToken.Invoke(null, [name, context])!)}\n");
            table.Append($"{shape} {Show(name)} secrets: {Outcome(() => AssignmentBuilder.SecretsFor(name!, context))}\n");
        }
        // A pipeline's step reaches its secrets through its stage, named as the run records them.
        foreach (var managed in new[] { false, true })
        foreach (var stage in new string?[] { "dns", "deploy", null })
        foreach (var name in Names())
        {
            var step = new Step { Name = name!, Kind = StepKinds.Agent, Stage = stage };
            table.Append($"pipeline{(managed ? " managed host key" : "")} step of {stage ?? "no stage"} {Show(name)} secrets: "
                + $"{Outcome(() => AssignmentBuilder.SecretsFor(step, Pipeline(managed)))}\n");
        }
        return table.ToString();
    }

    [Fact]
    public void Every_step_name_runs_holds_and_carries_what_it_did_before_the_registry()
    {
        var actual = Table().TrimEnd('\n').Split('\n');
        var expected = Expected.ReplaceLineEndings("\n").Split('\n');
        // Line by line, so a failure names the step and shape that changed.
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actual[i]);
    }

    private const string Expected = """
'apply' action: opentofu/apply mutating=True
'apply' token: proxmox.privileged-token
'approval' action: throws InvalidOperationException: The step 'approval' does not run on an agent.
'approval' token: proxmox.read-only-token
'approve-removal' action: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
'approve-removal' token: proxmox.read-only-token
'authorize-key' action: ansible/run mutating=True
'authorize-key' token: proxmox.read-only-token
'confirm-refused' action: ansible/run mutating=False
'confirm-refused' token: proxmox.read-only-token
'observe' action: proxmox/observe mutating=False
'observe' token: proxmox.read-only-token
'outcome-approval' action: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
'outcome-approval' token: proxmox.read-only-token
'plan' action: opentofu/preview mutating=False
'plan' token: proxmox.read-only-token
'playbook' action: ansible/run mutating=True
'playbook' token: proxmox.read-only-token
'playbook-approved' action: ansible/run mutating=True
'playbook-approved' token: proxmox.read-only-token
'preview' action: ansible/preview mutating=False
'preview' token: proxmox.read-only-token
'promote-key' action: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
'promote-key' token: proxmox.read-only-token
'protect' action: proxmox/set-protection mutating=True
'protect' token: proxmox.privileged-token
'remove-key' action: ansible/run mutating=True
'remove-key' token: proxmox.read-only-token
'reprotect' action: proxmox/set-protection mutating=True
'reprotect' token: proxmox.privileged-token
'unprotect' action: proxmox/set-protection mutating=True
'unprotect' token: proxmox.privileged-token
'verify' action: proxmox/verify-guest mutating=False
'verify' token: proxmox.read-only-token
'verify-absent' action: proxmox/verify-absent mutating=False
'verify-absent' token: proxmox.read-only-token
'verify-current' action: ansible/run mutating=False
'verify-current' token: proxmox.read-only-token
'verify-key' action: ansible/run mutating=False
'verify-key' token: proxmox.read-only-token
'verify-removal' action: ansible/run mutating=False
'verify-removal' token: proxmox.read-only-token
'no-such-step' action: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
'no-such-step' token: proxmox.read-only-token
'' action: throws InvalidOperationException: The step '' does not run on an agent.
'' token: proxmox.read-only-token
'Plan' action: throws InvalidOperationException: The step 'Plan' does not run on an agent.
'Plan' token: proxmox.read-only-token
'APPLY' action: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
'APPLY' token: proxmox.read-only-token
' verify' action: throws InvalidOperationException: The step ' verify' does not run on an agent.
' verify' token: proxmox.read-only-token
<null> action: throws InvalidOperationException: The step '' does not run on an agent.
<null> token: proxmox.read-only-token
provision split 'apply' token: proxmox.privileged-token
provision split 'apply' secrets: [proxmox.privileged-token]
provision split 'approval' token: proxmox.read-only-token
provision split 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
provision split 'approve-removal' token: proxmox.read-only-token
provision split 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
provision split 'authorize-key' token: proxmox.read-only-token
provision split 'authorize-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
provision split 'confirm-refused' token: proxmox.read-only-token
provision split 'confirm-refused' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
provision split 'observe' token: proxmox.read-only-token
provision split 'observe' secrets: [proxmox.read-only-token, proxmox.ca]
provision split 'outcome-approval' token: proxmox.read-only-token
provision split 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
provision split 'plan' token: proxmox.read-only-token
provision split 'plan' secrets: [proxmox.read-only-token]
provision split 'playbook' token: proxmox.read-only-token
provision split 'playbook' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
provision split 'playbook-approved' token: proxmox.read-only-token
provision split 'playbook-approved' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
provision split 'preview' token: proxmox.read-only-token
provision split 'preview' secrets: [ansible.host-key]
provision split 'promote-key' token: proxmox.read-only-token
provision split 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
provision split 'protect' token: proxmox.privileged-token
provision split 'protect' secrets: [proxmox.privileged-token, proxmox.ca]
provision split 'remove-key' token: proxmox.read-only-token
provision split 'remove-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
provision split 'reprotect' token: proxmox.privileged-token
provision split 'reprotect' secrets: [proxmox.privileged-token, proxmox.ca]
provision split 'unprotect' token: proxmox.privileged-token
provision split 'unprotect' secrets: [proxmox.privileged-token, proxmox.ca]
provision split 'verify' token: proxmox.read-only-token
provision split 'verify' secrets: [proxmox.read-only-token, proxmox.ca]
provision split 'verify-absent' token: proxmox.read-only-token
provision split 'verify-absent' secrets: [proxmox.read-only-token, proxmox.ca]
provision split 'verify-current' token: proxmox.read-only-token
provision split 'verify-current' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
provision split 'verify-key' token: proxmox.read-only-token
provision split 'verify-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
provision split 'verify-removal' token: proxmox.read-only-token
provision split 'verify-removal' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
provision split 'no-such-step' token: proxmox.read-only-token
provision split 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
provision split '' token: proxmox.read-only-token
provision split '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
provision split 'Plan' token: proxmox.read-only-token
provision split 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
provision split 'APPLY' token: proxmox.read-only-token
provision split 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
provision split ' verify' token: proxmox.read-only-token
provision split ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
provision split <null> token: proxmox.read-only-token
provision split <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
provision split without ca 'apply' token: proxmox.privileged-token
provision split without ca 'apply' secrets: [proxmox.privileged-token]
provision split without ca 'approval' token: proxmox.read-only-token
provision split without ca 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
provision split without ca 'approve-removal' token: proxmox.read-only-token
provision split without ca 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
provision split without ca 'authorize-key' token: proxmox.read-only-token
provision split without ca 'authorize-key' secrets: [proxmox.read-only-token, proxmox.privileged-token]
provision split without ca 'confirm-refused' token: proxmox.read-only-token
provision split without ca 'confirm-refused' secrets: [proxmox.read-only-token, proxmox.privileged-token]
provision split without ca 'observe' token: proxmox.read-only-token
provision split without ca 'observe' secrets: [proxmox.read-only-token]
provision split without ca 'outcome-approval' token: proxmox.read-only-token
provision split without ca 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
provision split without ca 'plan' token: proxmox.read-only-token
provision split without ca 'plan' secrets: [proxmox.read-only-token]
provision split without ca 'playbook' token: proxmox.read-only-token
provision split without ca 'playbook' secrets: [proxmox.read-only-token, proxmox.privileged-token]
provision split without ca 'playbook-approved' token: proxmox.read-only-token
provision split without ca 'playbook-approved' secrets: [proxmox.read-only-token, proxmox.privileged-token]
provision split without ca 'preview' token: proxmox.read-only-token
provision split without ca 'preview' secrets: [ansible.host-key]
provision split without ca 'promote-key' token: proxmox.read-only-token
provision split without ca 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
provision split without ca 'protect' token: proxmox.privileged-token
provision split without ca 'protect' secrets: [proxmox.privileged-token]
provision split without ca 'remove-key' token: proxmox.read-only-token
provision split without ca 'remove-key' secrets: [proxmox.read-only-token, proxmox.privileged-token]
provision split without ca 'reprotect' token: proxmox.privileged-token
provision split without ca 'reprotect' secrets: [proxmox.privileged-token]
provision split without ca 'unprotect' token: proxmox.privileged-token
provision split without ca 'unprotect' secrets: [proxmox.privileged-token]
provision split without ca 'verify' token: proxmox.read-only-token
provision split without ca 'verify' secrets: [proxmox.read-only-token]
provision split without ca 'verify-absent' token: proxmox.read-only-token
provision split without ca 'verify-absent' secrets: [proxmox.read-only-token]
provision split without ca 'verify-current' token: proxmox.read-only-token
provision split without ca 'verify-current' secrets: [proxmox.read-only-token, proxmox.privileged-token]
provision split without ca 'verify-key' token: proxmox.read-only-token
provision split without ca 'verify-key' secrets: [proxmox.read-only-token, proxmox.privileged-token]
provision split without ca 'verify-removal' token: proxmox.read-only-token
provision split without ca 'verify-removal' secrets: [proxmox.read-only-token, proxmox.privileged-token]
provision split without ca 'no-such-step' token: proxmox.read-only-token
provision split without ca 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
provision split without ca '' token: proxmox.read-only-token
provision split without ca '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
provision split without ca 'Plan' token: proxmox.read-only-token
provision split without ca 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
provision split without ca 'APPLY' token: proxmox.read-only-token
provision split without ca 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
provision split without ca ' verify' token: proxmox.read-only-token
provision split without ca ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
provision split without ca <null> token: proxmox.read-only-token
provision split without ca <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
provision shared 'apply' token: proxmox.shared-token
provision shared 'apply' secrets: [proxmox.shared-token]
provision shared 'approval' token: proxmox.shared-token
provision shared 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
provision shared 'approve-removal' token: proxmox.shared-token
provision shared 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
provision shared 'authorize-key' token: proxmox.shared-token
provision shared 'authorize-key' secrets: [proxmox.shared-token, proxmox.ca]
provision shared 'confirm-refused' token: proxmox.shared-token
provision shared 'confirm-refused' secrets: [proxmox.shared-token, proxmox.ca]
provision shared 'observe' token: proxmox.shared-token
provision shared 'observe' secrets: [proxmox.shared-token, proxmox.ca]
provision shared 'outcome-approval' token: proxmox.shared-token
provision shared 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
provision shared 'plan' token: proxmox.shared-token
provision shared 'plan' secrets: [proxmox.shared-token]
provision shared 'playbook' token: proxmox.shared-token
provision shared 'playbook' secrets: [proxmox.shared-token, proxmox.ca]
provision shared 'playbook-approved' token: proxmox.shared-token
provision shared 'playbook-approved' secrets: [proxmox.shared-token, proxmox.ca]
provision shared 'preview' token: proxmox.shared-token
provision shared 'preview' secrets: [ansible.host-key]
provision shared 'promote-key' token: proxmox.shared-token
provision shared 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
provision shared 'protect' token: proxmox.shared-token
provision shared 'protect' secrets: [proxmox.shared-token, proxmox.ca]
provision shared 'remove-key' token: proxmox.shared-token
provision shared 'remove-key' secrets: [proxmox.shared-token, proxmox.ca]
provision shared 'reprotect' token: proxmox.shared-token
provision shared 'reprotect' secrets: [proxmox.shared-token, proxmox.ca]
provision shared 'unprotect' token: proxmox.shared-token
provision shared 'unprotect' secrets: [proxmox.shared-token, proxmox.ca]
provision shared 'verify' token: proxmox.shared-token
provision shared 'verify' secrets: [proxmox.shared-token, proxmox.ca]
provision shared 'verify-absent' token: proxmox.shared-token
provision shared 'verify-absent' secrets: [proxmox.shared-token, proxmox.ca]
provision shared 'verify-current' token: proxmox.shared-token
provision shared 'verify-current' secrets: [proxmox.shared-token, proxmox.ca]
provision shared 'verify-key' token: proxmox.shared-token
provision shared 'verify-key' secrets: [proxmox.shared-token, proxmox.ca]
provision shared 'verify-removal' token: proxmox.shared-token
provision shared 'verify-removal' secrets: [proxmox.shared-token, proxmox.ca]
provision shared 'no-such-step' token: proxmox.shared-token
provision shared 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
provision shared '' token: proxmox.shared-token
provision shared '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
provision shared 'Plan' token: proxmox.shared-token
provision shared 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
provision shared 'APPLY' token: proxmox.shared-token
provision shared 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
provision shared ' verify' token: proxmox.shared-token
provision shared ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
provision shared <null> token: proxmox.shared-token
provision shared <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
provision split snippets 'apply' token: proxmox.privileged-token
provision split snippets 'apply' secrets: [proxmox.privileged-token, snippets.ssh-key, snippets.host-key]
provision split snippets 'approval' token: proxmox.read-only-token
provision split snippets 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
provision split snippets 'approve-removal' token: proxmox.read-only-token
provision split snippets 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
provision split snippets 'authorize-key' token: proxmox.read-only-token
provision split snippets 'authorize-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision split snippets 'confirm-refused' token: proxmox.read-only-token
provision split snippets 'confirm-refused' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision split snippets 'observe' token: proxmox.read-only-token
provision split snippets 'observe' secrets: [proxmox.read-only-token, proxmox.ca]
provision split snippets 'outcome-approval' token: proxmox.read-only-token
provision split snippets 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
provision split snippets 'plan' token: proxmox.read-only-token
provision split snippets 'plan' secrets: [proxmox.read-only-token, opentofu.host-key]
provision split snippets 'playbook' token: proxmox.read-only-token
provision split snippets 'playbook' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision split snippets 'playbook-approved' token: proxmox.read-only-token
provision split snippets 'playbook-approved' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision split snippets 'preview' token: proxmox.read-only-token
provision split snippets 'preview' secrets: [ansible.host-key]
provision split snippets 'promote-key' token: proxmox.read-only-token
provision split snippets 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
provision split snippets 'protect' token: proxmox.privileged-token
provision split snippets 'protect' secrets: [proxmox.privileged-token, proxmox.ca]
provision split snippets 'remove-key' token: proxmox.read-only-token
provision split snippets 'remove-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision split snippets 'reprotect' token: proxmox.privileged-token
provision split snippets 'reprotect' secrets: [proxmox.privileged-token, proxmox.ca]
provision split snippets 'unprotect' token: proxmox.privileged-token
provision split snippets 'unprotect' secrets: [proxmox.privileged-token, proxmox.ca]
provision split snippets 'verify' token: proxmox.read-only-token
provision split snippets 'verify' secrets: [proxmox.read-only-token, proxmox.ca]
provision split snippets 'verify-absent' token: proxmox.read-only-token
provision split snippets 'verify-absent' secrets: [proxmox.read-only-token, proxmox.ca]
provision split snippets 'verify-current' token: proxmox.read-only-token
provision split snippets 'verify-current' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision split snippets 'verify-key' token: proxmox.read-only-token
provision split snippets 'verify-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision split snippets 'verify-removal' token: proxmox.read-only-token
provision split snippets 'verify-removal' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision split snippets 'no-such-step' token: proxmox.read-only-token
provision split snippets 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
provision split snippets '' token: proxmox.read-only-token
provision split snippets '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
provision split snippets 'Plan' token: proxmox.read-only-token
provision split snippets 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
provision split snippets 'APPLY' token: proxmox.read-only-token
provision split snippets 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
provision split snippets ' verify' token: proxmox.read-only-token
provision split snippets ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
provision split snippets <null> token: proxmox.read-only-token
provision split snippets <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
provision shared snippets 'apply' token: proxmox.shared-token
provision shared snippets 'apply' secrets: [proxmox.shared-token, snippets.ssh-key, snippets.host-key]
provision shared snippets 'approval' token: proxmox.shared-token
provision shared snippets 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
provision shared snippets 'approve-removal' token: proxmox.shared-token
provision shared snippets 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
provision shared snippets 'authorize-key' token: proxmox.shared-token
provision shared snippets 'authorize-key' secrets: [proxmox.shared-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision shared snippets 'confirm-refused' token: proxmox.shared-token
provision shared snippets 'confirm-refused' secrets: [proxmox.shared-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision shared snippets 'observe' token: proxmox.shared-token
provision shared snippets 'observe' secrets: [proxmox.shared-token, proxmox.ca]
provision shared snippets 'outcome-approval' token: proxmox.shared-token
provision shared snippets 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
provision shared snippets 'plan' token: proxmox.shared-token
provision shared snippets 'plan' secrets: [proxmox.shared-token, opentofu.host-key]
provision shared snippets 'playbook' token: proxmox.shared-token
provision shared snippets 'playbook' secrets: [proxmox.shared-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision shared snippets 'playbook-approved' token: proxmox.shared-token
provision shared snippets 'playbook-approved' secrets: [proxmox.shared-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision shared snippets 'preview' token: proxmox.shared-token
provision shared snippets 'preview' secrets: [ansible.host-key]
provision shared snippets 'promote-key' token: proxmox.shared-token
provision shared snippets 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
provision shared snippets 'protect' token: proxmox.shared-token
provision shared snippets 'protect' secrets: [proxmox.shared-token, proxmox.ca]
provision shared snippets 'remove-key' token: proxmox.shared-token
provision shared snippets 'remove-key' secrets: [proxmox.shared-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision shared snippets 'reprotect' token: proxmox.shared-token
provision shared snippets 'reprotect' secrets: [proxmox.shared-token, proxmox.ca]
provision shared snippets 'unprotect' token: proxmox.shared-token
provision shared snippets 'unprotect' secrets: [proxmox.shared-token, proxmox.ca]
provision shared snippets 'verify' token: proxmox.shared-token
provision shared snippets 'verify' secrets: [proxmox.shared-token, proxmox.ca]
provision shared snippets 'verify-absent' token: proxmox.shared-token
provision shared snippets 'verify-absent' secrets: [proxmox.shared-token, proxmox.ca]
provision shared snippets 'verify-current' token: proxmox.shared-token
provision shared snippets 'verify-current' secrets: [proxmox.shared-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision shared snippets 'verify-key' token: proxmox.shared-token
provision shared snippets 'verify-key' secrets: [proxmox.shared-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision shared snippets 'verify-removal' token: proxmox.shared-token
provision shared snippets 'verify-removal' secrets: [proxmox.shared-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision shared snippets 'no-such-step' token: proxmox.shared-token
provision shared snippets 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
provision shared snippets '' token: proxmox.shared-token
provision shared snippets '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
provision shared snippets 'Plan' token: proxmox.shared-token
provision shared snippets 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
provision shared snippets 'APPLY' token: proxmox.shared-token
provision shared snippets 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
provision shared snippets ' verify' token: proxmox.shared-token
provision shared snippets ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
provision shared snippets <null> token: proxmox.shared-token
provision shared snippets <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
provision snippet env without upload 'apply' token: proxmox.privileged-token
provision snippet env without upload 'apply' secrets: [proxmox.privileged-token, snippets.ssh-key]
provision snippet env without upload 'approval' token: proxmox.read-only-token
provision snippet env without upload 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
provision snippet env without upload 'approve-removal' token: proxmox.read-only-token
provision snippet env without upload 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
provision snippet env without upload 'authorize-key' token: proxmox.read-only-token
provision snippet env without upload 'authorize-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision snippet env without upload 'confirm-refused' token: proxmox.read-only-token
provision snippet env without upload 'confirm-refused' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision snippet env without upload 'observe' token: proxmox.read-only-token
provision snippet env without upload 'observe' secrets: [proxmox.read-only-token, proxmox.ca]
provision snippet env without upload 'outcome-approval' token: proxmox.read-only-token
provision snippet env without upload 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
provision snippet env without upload 'plan' token: proxmox.read-only-token
provision snippet env without upload 'plan' secrets: [proxmox.read-only-token, opentofu.host-key]
provision snippet env without upload 'playbook' token: proxmox.read-only-token
provision snippet env without upload 'playbook' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision snippet env without upload 'playbook-approved' token: proxmox.read-only-token
provision snippet env without upload 'playbook-approved' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision snippet env without upload 'preview' token: proxmox.read-only-token
provision snippet env without upload 'preview' secrets: [ansible.host-key]
provision snippet env without upload 'promote-key' token: proxmox.read-only-token
provision snippet env without upload 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
provision snippet env without upload 'protect' token: proxmox.privileged-token
provision snippet env without upload 'protect' secrets: [proxmox.privileged-token, proxmox.ca]
provision snippet env without upload 'remove-key' token: proxmox.read-only-token
provision snippet env without upload 'remove-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision snippet env without upload 'reprotect' token: proxmox.privileged-token
provision snippet env without upload 'reprotect' secrets: [proxmox.privileged-token, proxmox.ca]
provision snippet env without upload 'unprotect' token: proxmox.privileged-token
provision snippet env without upload 'unprotect' secrets: [proxmox.privileged-token, proxmox.ca]
provision snippet env without upload 'verify' token: proxmox.read-only-token
provision snippet env without upload 'verify' secrets: [proxmox.read-only-token, proxmox.ca]
provision snippet env without upload 'verify-absent' token: proxmox.read-only-token
provision snippet env without upload 'verify-absent' secrets: [proxmox.read-only-token, proxmox.ca]
provision snippet env without upload 'verify-current' token: proxmox.read-only-token
provision snippet env without upload 'verify-current' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision snippet env without upload 'verify-key' token: proxmox.read-only-token
provision snippet env without upload 'verify-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision snippet env without upload 'verify-removal' token: proxmox.read-only-token
provision snippet env without upload 'verify-removal' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca, opentofu.host-key, snippets.ssh-key, snippets.host-key]
provision snippet env without upload 'no-such-step' token: proxmox.read-only-token
provision snippet env without upload 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
provision snippet env without upload '' token: proxmox.read-only-token
provision snippet env without upload '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
provision snippet env without upload 'Plan' token: proxmox.read-only-token
provision snippet env without upload 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
provision snippet env without upload 'APPLY' token: proxmox.read-only-token
provision snippet env without upload 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
provision snippet env without upload ' verify' token: proxmox.read-only-token
provision snippet env without upload ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
provision snippet env without upload <null> token: proxmox.read-only-token
provision snippet env without upload <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
provision built template 'apply' token: proxmox.privileged-token
provision built template 'apply' secrets: [proxmox.privileged-token]
provision built template 'approval' token: proxmox.read-only-token
provision built template 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
provision built template 'approve-removal' token: proxmox.read-only-token
provision built template 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
provision built template 'authorize-key' token: proxmox.read-only-token
provision built template 'authorize-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
provision built template 'confirm-refused' token: proxmox.read-only-token
provision built template 'confirm-refused' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
provision built template 'observe' token: proxmox.read-only-token
provision built template 'observe' secrets: [proxmox.read-only-token, proxmox.ca]
provision built template 'outcome-approval' token: proxmox.read-only-token
provision built template 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
provision built template 'plan' token: proxmox.read-only-token
provision built template 'plan' secrets: [proxmox.read-only-token]
provision built template 'playbook' token: proxmox.read-only-token
provision built template 'playbook' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
provision built template 'playbook-approved' token: proxmox.read-only-token
provision built template 'playbook-approved' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
provision built template 'preview' token: proxmox.read-only-token
provision built template 'preview' secrets: [ansible.host-key]
provision built template 'promote-key' token: proxmox.read-only-token
provision built template 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
provision built template 'protect' token: proxmox.privileged-token
provision built template 'protect' secrets: [proxmox.privileged-token, proxmox.ca]
provision built template 'remove-key' token: proxmox.read-only-token
provision built template 'remove-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
provision built template 'reprotect' token: proxmox.privileged-token
provision built template 'reprotect' secrets: [proxmox.privileged-token, proxmox.ca]
provision built template 'unprotect' token: proxmox.privileged-token
provision built template 'unprotect' secrets: [proxmox.privileged-token, proxmox.ca]
provision built template 'verify' token: proxmox.read-only-token
provision built template 'verify' secrets: [proxmox.read-only-token, proxmox.ca]
provision built template 'verify-absent' token: proxmox.read-only-token
provision built template 'verify-absent' secrets: [proxmox.read-only-token, proxmox.ca]
provision built template 'verify-current' token: proxmox.read-only-token
provision built template 'verify-current' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
provision built template 'verify-key' token: proxmox.read-only-token
provision built template 'verify-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
provision built template 'verify-removal' token: proxmox.read-only-token
provision built template 'verify-removal' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
provision built template 'no-such-step' token: proxmox.read-only-token
provision built template 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
provision built template '' token: proxmox.read-only-token
provision built template '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
provision built template 'Plan' token: proxmox.read-only-token
provision built template 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
provision built template 'APPLY' token: proxmox.read-only-token
provision built template 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
provision built template ' verify' token: proxmox.read-only-token
provision built template ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
provision built template <null> token: proxmox.read-only-token
provision built template <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
retire split 'apply' token: proxmox.privileged-token
retire split 'apply' secrets: [proxmox.privileged-token]
retire split 'approval' token: proxmox.read-only-token
retire split 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
retire split 'approve-removal' token: proxmox.read-only-token
retire split 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
retire split 'authorize-key' token: proxmox.read-only-token
retire split 'authorize-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
retire split 'confirm-refused' token: proxmox.read-only-token
retire split 'confirm-refused' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
retire split 'observe' token: proxmox.read-only-token
retire split 'observe' secrets: [proxmox.read-only-token, proxmox.ca]
retire split 'outcome-approval' token: proxmox.read-only-token
retire split 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
retire split 'plan' token: proxmox.read-only-token
retire split 'plan' secrets: [proxmox.read-only-token]
retire split 'playbook' token: proxmox.read-only-token
retire split 'playbook' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
retire split 'playbook-approved' token: proxmox.read-only-token
retire split 'playbook-approved' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
retire split 'preview' token: proxmox.read-only-token
retire split 'preview' secrets: [ansible.host-key]
retire split 'promote-key' token: proxmox.read-only-token
retire split 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
retire split 'protect' token: proxmox.privileged-token
retire split 'protect' secrets: [proxmox.privileged-token, proxmox.ca]
retire split 'remove-key' token: proxmox.read-only-token
retire split 'remove-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
retire split 'reprotect' token: proxmox.privileged-token
retire split 'reprotect' secrets: [proxmox.privileged-token, proxmox.ca]
retire split 'unprotect' token: proxmox.privileged-token
retire split 'unprotect' secrets: [proxmox.privileged-token, proxmox.ca]
retire split 'verify' token: proxmox.read-only-token
retire split 'verify' secrets: [proxmox.read-only-token, proxmox.ca]
retire split 'verify-absent' token: proxmox.read-only-token
retire split 'verify-absent' secrets: [proxmox.read-only-token, proxmox.ca]
retire split 'verify-current' token: proxmox.read-only-token
retire split 'verify-current' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
retire split 'verify-key' token: proxmox.read-only-token
retire split 'verify-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
retire split 'verify-removal' token: proxmox.read-only-token
retire split 'verify-removal' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
retire split 'no-such-step' token: proxmox.read-only-token
retire split 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
retire split '' token: proxmox.read-only-token
retire split '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
retire split 'Plan' token: proxmox.read-only-token
retire split 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
retire split 'APPLY' token: proxmox.read-only-token
retire split 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
retire split ' verify' token: proxmox.read-only-token
retire split ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
retire split <null> token: proxmox.read-only-token
retire split <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
retire shared 'apply' token: proxmox.shared-token
retire shared 'apply' secrets: [proxmox.shared-token]
retire shared 'approval' token: proxmox.shared-token
retire shared 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
retire shared 'approve-removal' token: proxmox.shared-token
retire shared 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
retire shared 'authorize-key' token: proxmox.shared-token
retire shared 'authorize-key' secrets: [proxmox.shared-token, proxmox.ca]
retire shared 'confirm-refused' token: proxmox.shared-token
retire shared 'confirm-refused' secrets: [proxmox.shared-token, proxmox.ca]
retire shared 'observe' token: proxmox.shared-token
retire shared 'observe' secrets: [proxmox.shared-token, proxmox.ca]
retire shared 'outcome-approval' token: proxmox.shared-token
retire shared 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
retire shared 'plan' token: proxmox.shared-token
retire shared 'plan' secrets: [proxmox.shared-token]
retire shared 'playbook' token: proxmox.shared-token
retire shared 'playbook' secrets: [proxmox.shared-token, proxmox.ca]
retire shared 'playbook-approved' token: proxmox.shared-token
retire shared 'playbook-approved' secrets: [proxmox.shared-token, proxmox.ca]
retire shared 'preview' token: proxmox.shared-token
retire shared 'preview' secrets: [ansible.host-key]
retire shared 'promote-key' token: proxmox.shared-token
retire shared 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
retire shared 'protect' token: proxmox.shared-token
retire shared 'protect' secrets: [proxmox.shared-token, proxmox.ca]
retire shared 'remove-key' token: proxmox.shared-token
retire shared 'remove-key' secrets: [proxmox.shared-token, proxmox.ca]
retire shared 'reprotect' token: proxmox.shared-token
retire shared 'reprotect' secrets: [proxmox.shared-token, proxmox.ca]
retire shared 'unprotect' token: proxmox.shared-token
retire shared 'unprotect' secrets: [proxmox.shared-token, proxmox.ca]
retire shared 'verify' token: proxmox.shared-token
retire shared 'verify' secrets: [proxmox.shared-token, proxmox.ca]
retire shared 'verify-absent' token: proxmox.shared-token
retire shared 'verify-absent' secrets: [proxmox.shared-token, proxmox.ca]
retire shared 'verify-current' token: proxmox.shared-token
retire shared 'verify-current' secrets: [proxmox.shared-token, proxmox.ca]
retire shared 'verify-key' token: proxmox.shared-token
retire shared 'verify-key' secrets: [proxmox.shared-token, proxmox.ca]
retire shared 'verify-removal' token: proxmox.shared-token
retire shared 'verify-removal' secrets: [proxmox.shared-token, proxmox.ca]
retire shared 'no-such-step' token: proxmox.shared-token
retire shared 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
retire shared '' token: proxmox.shared-token
retire shared '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
retire shared 'Plan' token: proxmox.shared-token
retire shared 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
retire shared 'APPLY' token: proxmox.shared-token
retire shared 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
retire shared ' verify' token: proxmox.shared-token
retire shared ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
retire shared <null> token: proxmox.shared-token
retire shared <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
attach split 'apply' token: proxmox.privileged-token
attach split 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
attach split 'approval' token: proxmox.read-only-token
attach split 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
attach split 'approve-removal' token: proxmox.read-only-token
attach split 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
attach split 'authorize-key' token: proxmox.read-only-token
attach split 'authorize-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
attach split 'confirm-refused' token: proxmox.read-only-token
attach split 'confirm-refused' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
attach split 'observe' token: proxmox.read-only-token
attach split 'observe' secrets: [proxmox.read-only-token, proxmox.ca]
attach split 'outcome-approval' token: proxmox.read-only-token
attach split 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
attach split 'plan' token: proxmox.read-only-token
attach split 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
attach split 'playbook' token: proxmox.read-only-token
attach split 'playbook' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
attach split 'playbook-approved' token: proxmox.read-only-token
attach split 'playbook-approved' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
attach split 'preview' token: proxmox.read-only-token
attach split 'preview' secrets: [ansible.host-key]
attach split 'promote-key' token: proxmox.read-only-token
attach split 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
attach split 'protect' token: proxmox.privileged-token
attach split 'protect' secrets: [proxmox.privileged-token, proxmox.ca]
attach split 'remove-key' token: proxmox.read-only-token
attach split 'remove-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
attach split 'reprotect' token: proxmox.privileged-token
attach split 'reprotect' secrets: [proxmox.privileged-token, proxmox.ca]
attach split 'unprotect' token: proxmox.privileged-token
attach split 'unprotect' secrets: [proxmox.privileged-token, proxmox.ca]
attach split 'verify' token: proxmox.read-only-token
attach split 'verify' secrets: [proxmox.read-only-token, proxmox.ca]
attach split 'verify-absent' token: proxmox.read-only-token
attach split 'verify-absent' secrets: [proxmox.read-only-token, proxmox.ca]
attach split 'verify-current' token: proxmox.read-only-token
attach split 'verify-current' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
attach split 'verify-key' token: proxmox.read-only-token
attach split 'verify-key' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
attach split 'verify-removal' token: proxmox.read-only-token
attach split 'verify-removal' secrets: [proxmox.read-only-token, proxmox.privileged-token, proxmox.ca]
attach split 'no-such-step' token: proxmox.read-only-token
attach split 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
attach split '' token: proxmox.read-only-token
attach split '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
attach split 'Plan' token: proxmox.read-only-token
attach split 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
attach split 'APPLY' token: proxmox.read-only-token
attach split 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
attach split ' verify' token: proxmox.read-only-token
attach split ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
attach split <null> token: proxmox.read-only-token
attach split <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
attach shared without ca 'apply' token: proxmox.shared-token
attach shared without ca 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
attach shared without ca 'approval' token: proxmox.shared-token
attach shared without ca 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
attach shared without ca 'approve-removal' token: proxmox.shared-token
attach shared without ca 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
attach shared without ca 'authorize-key' token: proxmox.shared-token
attach shared without ca 'authorize-key' secrets: [proxmox.shared-token]
attach shared without ca 'confirm-refused' token: proxmox.shared-token
attach shared without ca 'confirm-refused' secrets: [proxmox.shared-token]
attach shared without ca 'observe' token: proxmox.shared-token
attach shared without ca 'observe' secrets: [proxmox.shared-token]
attach shared without ca 'outcome-approval' token: proxmox.shared-token
attach shared without ca 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
attach shared without ca 'plan' token: proxmox.shared-token
attach shared without ca 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
attach shared without ca 'playbook' token: proxmox.shared-token
attach shared without ca 'playbook' secrets: [proxmox.shared-token]
attach shared without ca 'playbook-approved' token: proxmox.shared-token
attach shared without ca 'playbook-approved' secrets: [proxmox.shared-token]
attach shared without ca 'preview' token: proxmox.shared-token
attach shared without ca 'preview' secrets: [ansible.host-key]
attach shared without ca 'promote-key' token: proxmox.shared-token
attach shared without ca 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
attach shared without ca 'protect' token: proxmox.shared-token
attach shared without ca 'protect' secrets: [proxmox.shared-token]
attach shared without ca 'remove-key' token: proxmox.shared-token
attach shared without ca 'remove-key' secrets: [proxmox.shared-token]
attach shared without ca 'reprotect' token: proxmox.shared-token
attach shared without ca 'reprotect' secrets: [proxmox.shared-token]
attach shared without ca 'unprotect' token: proxmox.shared-token
attach shared without ca 'unprotect' secrets: [proxmox.shared-token]
attach shared without ca 'verify' token: proxmox.shared-token
attach shared without ca 'verify' secrets: [proxmox.shared-token]
attach shared without ca 'verify-absent' token: proxmox.shared-token
attach shared without ca 'verify-absent' secrets: [proxmox.shared-token]
attach shared without ca 'verify-current' token: proxmox.shared-token
attach shared without ca 'verify-current' secrets: [proxmox.shared-token]
attach shared without ca 'verify-key' token: proxmox.shared-token
attach shared without ca 'verify-key' secrets: [proxmox.shared-token]
attach shared without ca 'verify-removal' token: proxmox.shared-token
attach shared without ca 'verify-removal' secrets: [proxmox.shared-token]
attach shared without ca 'no-such-step' token: proxmox.shared-token
attach shared without ca 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
attach shared without ca '' token: proxmox.shared-token
attach shared without ca '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
attach shared without ca 'Plan' token: proxmox.shared-token
attach shared without ca 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
attach shared without ca 'APPLY' token: proxmox.shared-token
attach shared without ca 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
attach shared without ca ' verify' token: proxmox.shared-token
attach shared without ca ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
attach shared without ca <null> token: proxmox.shared-token
attach shared without ca <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
configure 'apply' token: proxmox.privileged-token
configure 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
configure 'approval' token: proxmox.read-only-token
configure 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
configure 'approve-removal' token: proxmox.read-only-token
configure 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
configure 'authorize-key' token: proxmox.read-only-token
configure 'authorize-key' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key, ansible.host-key]
configure 'confirm-refused' token: proxmox.read-only-token
configure 'confirm-refused' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key, ansible.host-key]
configure 'observe' token: proxmox.read-only-token
configure 'observe' secrets: [proxmox.read-only-token]
configure 'outcome-approval' token: proxmox.read-only-token
configure 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
configure 'plan' token: proxmox.read-only-token
configure 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
configure 'playbook' token: proxmox.read-only-token
configure 'playbook' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key, ansible.host-key]
configure 'playbook-approved' token: proxmox.read-only-token
configure 'playbook-approved' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key, ansible.host-key]
configure 'preview' token: proxmox.read-only-token
configure 'preview' secrets: [ansible.host-key]
configure 'promote-key' token: proxmox.read-only-token
configure 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
configure 'protect' token: proxmox.privileged-token
configure 'protect' secrets: [proxmox.privileged-token]
configure 'remove-key' token: proxmox.read-only-token
configure 'remove-key' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key, ansible.host-key]
configure 'reprotect' token: proxmox.privileged-token
configure 'reprotect' secrets: [proxmox.privileged-token]
configure 'unprotect' token: proxmox.privileged-token
configure 'unprotect' secrets: [proxmox.privileged-token]
configure 'verify' token: proxmox.read-only-token
configure 'verify' secrets: [proxmox.read-only-token]
configure 'verify-absent' token: proxmox.read-only-token
configure 'verify-absent' secrets: [proxmox.read-only-token]
configure 'verify-current' token: proxmox.read-only-token
configure 'verify-current' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key, ansible.host-key]
configure 'verify-key' token: proxmox.read-only-token
configure 'verify-key' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key, ansible.host-key]
configure 'verify-removal' token: proxmox.read-only-token
configure 'verify-removal' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key, ansible.host-key]
configure 'no-such-step' token: proxmox.read-only-token
configure 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
configure '' token: proxmox.read-only-token
configure '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
configure 'Plan' token: proxmox.read-only-token
configure 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
configure 'APPLY' token: proxmox.read-only-token
configure 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
configure ' verify' token: proxmox.read-only-token
configure ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
configure <null> token: proxmox.read-only-token
configure <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
configure destructive 'apply' token: proxmox.privileged-token
configure destructive 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
configure destructive 'approval' token: proxmox.read-only-token
configure destructive 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
configure destructive 'approve-removal' token: proxmox.read-only-token
configure destructive 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
configure destructive 'authorize-key' token: proxmox.read-only-token
configure destructive 'authorize-key' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key, ansible.host-key]
configure destructive 'confirm-refused' token: proxmox.read-only-token
configure destructive 'confirm-refused' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key, ansible.host-key]
configure destructive 'observe' token: proxmox.read-only-token
configure destructive 'observe' secrets: [proxmox.read-only-token]
configure destructive 'outcome-approval' token: proxmox.read-only-token
configure destructive 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
configure destructive 'plan' token: proxmox.read-only-token
configure destructive 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
configure destructive 'playbook' token: proxmox.read-only-token
configure destructive 'playbook' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key, ansible.host-key]
configure destructive 'playbook-approved' token: proxmox.read-only-token
configure destructive 'playbook-approved' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key, ansible.host-key]
configure destructive 'preview' token: proxmox.read-only-token
configure destructive 'preview' secrets: [ansible.host-key]
configure destructive 'promote-key' token: proxmox.read-only-token
configure destructive 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
configure destructive 'protect' token: proxmox.privileged-token
configure destructive 'protect' secrets: [proxmox.privileged-token]
configure destructive 'remove-key' token: proxmox.read-only-token
configure destructive 'remove-key' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key, ansible.host-key]
configure destructive 'reprotect' token: proxmox.privileged-token
configure destructive 'reprotect' secrets: [proxmox.privileged-token]
configure destructive 'unprotect' token: proxmox.privileged-token
configure destructive 'unprotect' secrets: [proxmox.privileged-token]
configure destructive 'verify' token: proxmox.read-only-token
configure destructive 'verify' secrets: [proxmox.read-only-token]
configure destructive 'verify-absent' token: proxmox.read-only-token
configure destructive 'verify-absent' secrets: [proxmox.read-only-token]
configure destructive 'verify-current' token: proxmox.read-only-token
configure destructive 'verify-current' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key, ansible.host-key]
configure destructive 'verify-key' token: proxmox.read-only-token
configure destructive 'verify-key' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key, ansible.host-key]
configure destructive 'verify-removal' token: proxmox.read-only-token
configure destructive 'verify-removal' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key, ansible.host-key]
configure destructive 'no-such-step' token: proxmox.read-only-token
configure destructive 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
configure destructive '' token: proxmox.read-only-token
configure destructive '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
configure destructive 'Plan' token: proxmox.read-only-token
configure destructive 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
configure destructive 'APPLY' token: proxmox.read-only-token
configure destructive 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
configure destructive ' verify' token: proxmox.read-only-token
configure destructive ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
configure destructive <null> token: proxmox.read-only-token
configure destructive <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
configure managed host key 'apply' token: proxmox.privileged-token
configure managed host key 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
configure managed host key 'approval' token: proxmox.read-only-token
configure managed host key 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
configure managed host key 'approve-removal' token: proxmox.read-only-token
configure managed host key 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
configure managed host key 'authorize-key' token: proxmox.read-only-token
configure managed host key 'authorize-key' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key]
configure managed host key 'confirm-refused' token: proxmox.read-only-token
configure managed host key 'confirm-refused' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key]
configure managed host key 'observe' token: proxmox.read-only-token
configure managed host key 'observe' secrets: [proxmox.read-only-token]
configure managed host key 'outcome-approval' token: proxmox.read-only-token
configure managed host key 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
configure managed host key 'plan' token: proxmox.read-only-token
configure managed host key 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
configure managed host key 'playbook' token: proxmox.read-only-token
configure managed host key 'playbook' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key]
configure managed host key 'playbook-approved' token: proxmox.read-only-token
configure managed host key 'playbook-approved' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key]
configure managed host key 'preview' token: proxmox.read-only-token
configure managed host key 'preview' secrets: []
configure managed host key 'promote-key' token: proxmox.read-only-token
configure managed host key 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
configure managed host key 'protect' token: proxmox.privileged-token
configure managed host key 'protect' secrets: [proxmox.privileged-token]
configure managed host key 'remove-key' token: proxmox.read-only-token
configure managed host key 'remove-key' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key]
configure managed host key 'reprotect' token: proxmox.privileged-token
configure managed host key 'reprotect' secrets: [proxmox.privileged-token]
configure managed host key 'unprotect' token: proxmox.privileged-token
configure managed host key 'unprotect' secrets: [proxmox.privileged-token]
configure managed host key 'verify' token: proxmox.read-only-token
configure managed host key 'verify' secrets: [proxmox.read-only-token]
configure managed host key 'verify-absent' token: proxmox.read-only-token
configure managed host key 'verify-absent' secrets: [proxmox.read-only-token]
configure managed host key 'verify-current' token: proxmox.read-only-token
configure managed host key 'verify-current' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key]
configure managed host key 'verify-key' token: proxmox.read-only-token
configure managed host key 'verify-key' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key]
configure managed host key 'verify-removal' token: proxmox.read-only-token
configure managed host key 'verify-removal' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.file.tls_key]
configure managed host key 'no-such-step' token: proxmox.read-only-token
configure managed host key 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
configure managed host key '' token: proxmox.read-only-token
configure managed host key '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
configure managed host key 'Plan' token: proxmox.read-only-token
configure managed host key 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
configure managed host key 'APPLY' token: proxmox.read-only-token
configure managed host key 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
configure managed host key ' verify' token: proxmox.read-only-token
configure managed host key ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
configure managed host key <null> token: proxmox.read-only-token
configure managed host key <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
rotate-access 'apply' token: proxmox.privileged-token
rotate-access 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
rotate-access 'approval' token: proxmox.read-only-token
rotate-access 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
rotate-access 'approve-removal' token: proxmox.read-only-token
rotate-access 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
rotate-access 'authorize-key' token: proxmox.read-only-token
rotate-access 'authorize-key' secrets: [ansible.host-key]
rotate-access 'confirm-refused' token: proxmox.read-only-token
rotate-access 'confirm-refused' secrets: [ansible.host-key]
rotate-access 'observe' token: proxmox.read-only-token
rotate-access 'observe' secrets: [proxmox.read-only-token]
rotate-access 'outcome-approval' token: proxmox.read-only-token
rotate-access 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
rotate-access 'plan' token: proxmox.read-only-token
rotate-access 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
rotate-access 'playbook' token: proxmox.read-only-token
rotate-access 'playbook' secrets: [ansible.host-key]
rotate-access 'playbook-approved' token: proxmox.read-only-token
rotate-access 'playbook-approved' secrets: [ansible.host-key]
rotate-access 'preview' token: proxmox.read-only-token
rotate-access 'preview' secrets: [ansible.host-key]
rotate-access 'promote-key' token: proxmox.read-only-token
rotate-access 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
rotate-access 'protect' token: proxmox.privileged-token
rotate-access 'protect' secrets: [proxmox.privileged-token]
rotate-access 'remove-key' token: proxmox.read-only-token
rotate-access 'remove-key' secrets: [ansible.host-key]
rotate-access 'reprotect' token: proxmox.privileged-token
rotate-access 'reprotect' secrets: [proxmox.privileged-token]
rotate-access 'unprotect' token: proxmox.privileged-token
rotate-access 'unprotect' secrets: [proxmox.privileged-token]
rotate-access 'verify' token: proxmox.read-only-token
rotate-access 'verify' secrets: [proxmox.read-only-token]
rotate-access 'verify-absent' token: proxmox.read-only-token
rotate-access 'verify-absent' secrets: [proxmox.read-only-token]
rotate-access 'verify-current' token: proxmox.read-only-token
rotate-access 'verify-current' secrets: [ansible.host-key]
rotate-access 'verify-key' token: proxmox.read-only-token
rotate-access 'verify-key' secrets: [ansible.host-key]
rotate-access 'verify-removal' token: proxmox.read-only-token
rotate-access 'verify-removal' secrets: [ansible.host-key]
rotate-access 'no-such-step' token: proxmox.read-only-token
rotate-access 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
rotate-access '' token: proxmox.read-only-token
rotate-access '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
rotate-access 'Plan' token: proxmox.read-only-token
rotate-access 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
rotate-access 'APPLY' token: proxmox.read-only-token
rotate-access 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
rotate-access ' verify' token: proxmox.read-only-token
rotate-access ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
rotate-access <null> token: proxmox.read-only-token
rotate-access <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
rotate-access managed host key 'apply' token: proxmox.privileged-token
rotate-access managed host key 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
rotate-access managed host key 'approval' token: proxmox.read-only-token
rotate-access managed host key 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
rotate-access managed host key 'approve-removal' token: proxmox.read-only-token
rotate-access managed host key 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
rotate-access managed host key 'authorize-key' token: proxmox.read-only-token
rotate-access managed host key 'authorize-key' secrets: []
rotate-access managed host key 'confirm-refused' token: proxmox.read-only-token
rotate-access managed host key 'confirm-refused' secrets: []
rotate-access managed host key 'observe' token: proxmox.read-only-token
rotate-access managed host key 'observe' secrets: [proxmox.read-only-token]
rotate-access managed host key 'outcome-approval' token: proxmox.read-only-token
rotate-access managed host key 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
rotate-access managed host key 'plan' token: proxmox.read-only-token
rotate-access managed host key 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
rotate-access managed host key 'playbook' token: proxmox.read-only-token
rotate-access managed host key 'playbook' secrets: []
rotate-access managed host key 'playbook-approved' token: proxmox.read-only-token
rotate-access managed host key 'playbook-approved' secrets: []
rotate-access managed host key 'preview' token: proxmox.read-only-token
rotate-access managed host key 'preview' secrets: []
rotate-access managed host key 'promote-key' token: proxmox.read-only-token
rotate-access managed host key 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
rotate-access managed host key 'protect' token: proxmox.privileged-token
rotate-access managed host key 'protect' secrets: [proxmox.privileged-token]
rotate-access managed host key 'remove-key' token: proxmox.read-only-token
rotate-access managed host key 'remove-key' secrets: []
rotate-access managed host key 'reprotect' token: proxmox.privileged-token
rotate-access managed host key 'reprotect' secrets: [proxmox.privileged-token]
rotate-access managed host key 'unprotect' token: proxmox.privileged-token
rotate-access managed host key 'unprotect' secrets: [proxmox.privileged-token]
rotate-access managed host key 'verify' token: proxmox.read-only-token
rotate-access managed host key 'verify' secrets: [proxmox.read-only-token]
rotate-access managed host key 'verify-absent' token: proxmox.read-only-token
rotate-access managed host key 'verify-absent' secrets: [proxmox.read-only-token]
rotate-access managed host key 'verify-current' token: proxmox.read-only-token
rotate-access managed host key 'verify-current' secrets: []
rotate-access managed host key 'verify-key' token: proxmox.read-only-token
rotate-access managed host key 'verify-key' secrets: []
rotate-access managed host key 'verify-removal' token: proxmox.read-only-token
rotate-access managed host key 'verify-removal' secrets: []
rotate-access managed host key 'no-such-step' token: proxmox.read-only-token
rotate-access managed host key 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
rotate-access managed host key '' token: proxmox.read-only-token
rotate-access managed host key '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
rotate-access managed host key 'Plan' token: proxmox.read-only-token
rotate-access managed host key 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
rotate-access managed host key 'APPLY' token: proxmox.read-only-token
rotate-access managed host key 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
rotate-access managed host key ' verify' token: proxmox.read-only-token
rotate-access managed host key ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
rotate-access managed host key <null> token: proxmox.read-only-token
rotate-access managed host key <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline 'apply' token: proxmox.privileged-token
pipeline 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
pipeline 'approval' token: proxmox.read-only-token
pipeline 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
pipeline 'approve-removal' token: proxmox.read-only-token
pipeline 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
pipeline 'authorize-key' token: proxmox.read-only-token
pipeline 'authorize-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline 'confirm-refused' token: proxmox.read-only-token
pipeline 'confirm-refused' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline 'observe' token: proxmox.read-only-token
pipeline 'observe' secrets: [proxmox.read-only-token]
pipeline 'outcome-approval' token: proxmox.read-only-token
pipeline 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
pipeline 'plan' token: proxmox.read-only-token
pipeline 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
pipeline 'playbook' token: proxmox.read-only-token
pipeline 'playbook' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline 'playbook-approved' token: proxmox.read-only-token
pipeline 'playbook-approved' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline 'preview' token: proxmox.read-only-token
pipeline 'preview' secrets: [ansible.host-key]
pipeline 'promote-key' token: proxmox.read-only-token
pipeline 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
pipeline 'protect' token: proxmox.privileged-token
pipeline 'protect' secrets: [proxmox.privileged-token]
pipeline 'remove-key' token: proxmox.read-only-token
pipeline 'remove-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline 'reprotect' token: proxmox.privileged-token
pipeline 'reprotect' secrets: [proxmox.privileged-token]
pipeline 'unprotect' token: proxmox.privileged-token
pipeline 'unprotect' secrets: [proxmox.privileged-token]
pipeline 'verify' token: proxmox.read-only-token
pipeline 'verify' secrets: [proxmox.read-only-token]
pipeline 'verify-absent' token: proxmox.read-only-token
pipeline 'verify-absent' secrets: [proxmox.read-only-token]
pipeline 'verify-current' token: proxmox.read-only-token
pipeline 'verify-current' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline 'verify-key' token: proxmox.read-only-token
pipeline 'verify-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline 'verify-removal' token: proxmox.read-only-token
pipeline 'verify-removal' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline 'no-such-step' token: proxmox.read-only-token
pipeline 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
pipeline '' token: proxmox.read-only-token
pipeline '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline 'Plan' token: proxmox.read-only-token
pipeline 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
pipeline 'APPLY' token: proxmox.read-only-token
pipeline 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
pipeline ' verify' token: proxmox.read-only-token
pipeline ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
pipeline <null> token: proxmox.read-only-token
pipeline <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline managed host key 'apply' token: proxmox.privileged-token
pipeline managed host key 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
pipeline managed host key 'approval' token: proxmox.read-only-token
pipeline managed host key 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
pipeline managed host key 'approve-removal' token: proxmox.read-only-token
pipeline managed host key 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
pipeline managed host key 'authorize-key' token: proxmox.read-only-token
pipeline managed host key 'authorize-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key 'confirm-refused' token: proxmox.read-only-token
pipeline managed host key 'confirm-refused' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key 'observe' token: proxmox.read-only-token
pipeline managed host key 'observe' secrets: [proxmox.read-only-token]
pipeline managed host key 'outcome-approval' token: proxmox.read-only-token
pipeline managed host key 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
pipeline managed host key 'plan' token: proxmox.read-only-token
pipeline managed host key 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
pipeline managed host key 'playbook' token: proxmox.read-only-token
pipeline managed host key 'playbook' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key 'playbook-approved' token: proxmox.read-only-token
pipeline managed host key 'playbook-approved' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key 'preview' token: proxmox.read-only-token
pipeline managed host key 'preview' secrets: []
pipeline managed host key 'promote-key' token: proxmox.read-only-token
pipeline managed host key 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
pipeline managed host key 'protect' token: proxmox.privileged-token
pipeline managed host key 'protect' secrets: [proxmox.privileged-token]
pipeline managed host key 'remove-key' token: proxmox.read-only-token
pipeline managed host key 'remove-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key 'reprotect' token: proxmox.privileged-token
pipeline managed host key 'reprotect' secrets: [proxmox.privileged-token]
pipeline managed host key 'unprotect' token: proxmox.privileged-token
pipeline managed host key 'unprotect' secrets: [proxmox.privileged-token]
pipeline managed host key 'verify' token: proxmox.read-only-token
pipeline managed host key 'verify' secrets: [proxmox.read-only-token]
pipeline managed host key 'verify-absent' token: proxmox.read-only-token
pipeline managed host key 'verify-absent' secrets: [proxmox.read-only-token]
pipeline managed host key 'verify-current' token: proxmox.read-only-token
pipeline managed host key 'verify-current' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key 'verify-key' token: proxmox.read-only-token
pipeline managed host key 'verify-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key 'verify-removal' token: proxmox.read-only-token
pipeline managed host key 'verify-removal' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key 'no-such-step' token: proxmox.read-only-token
pipeline managed host key 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
pipeline managed host key '' token: proxmox.read-only-token
pipeline managed host key '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline managed host key 'Plan' token: proxmox.read-only-token
pipeline managed host key 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
pipeline managed host key 'APPLY' token: proxmox.read-only-token
pipeline managed host key 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
pipeline managed host key ' verify' token: proxmox.read-only-token
pipeline managed host key ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
pipeline managed host key <null> token: proxmox.read-only-token
pipeline managed host key <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline stage dns 'apply' token: proxmox.privileged-token
pipeline stage dns 'apply' secrets: []
pipeline stage dns 'approval' token: proxmox.read-only-token
pipeline stage dns 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
pipeline stage dns 'approve-removal' token: proxmox.read-only-token
pipeline stage dns 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
pipeline stage dns 'authorize-key' token: proxmox.read-only-token
pipeline stage dns 'authorize-key' secrets: []
pipeline stage dns 'confirm-refused' token: proxmox.read-only-token
pipeline stage dns 'confirm-refused' secrets: []
pipeline stage dns 'observe' token: proxmox.read-only-token
pipeline stage dns 'observe' secrets: [proxmox.read-only-token]
pipeline stage dns 'outcome-approval' token: proxmox.read-only-token
pipeline stage dns 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
pipeline stage dns 'plan' token: proxmox.read-only-token
pipeline stage dns 'plan' secrets: []
pipeline stage dns 'playbook' token: proxmox.read-only-token
pipeline stage dns 'playbook' secrets: []
pipeline stage dns 'playbook-approved' token: proxmox.read-only-token
pipeline stage dns 'playbook-approved' secrets: []
pipeline stage dns 'preview' token: proxmox.read-only-token
pipeline stage dns 'preview' secrets: [ansible.host-key]
pipeline stage dns 'promote-key' token: proxmox.read-only-token
pipeline stage dns 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
pipeline stage dns 'protect' token: proxmox.privileged-token
pipeline stage dns 'protect' secrets: [proxmox.privileged-token]
pipeline stage dns 'remove-key' token: proxmox.read-only-token
pipeline stage dns 'remove-key' secrets: []
pipeline stage dns 'reprotect' token: proxmox.privileged-token
pipeline stage dns 'reprotect' secrets: [proxmox.privileged-token]
pipeline stage dns 'unprotect' token: proxmox.privileged-token
pipeline stage dns 'unprotect' secrets: [proxmox.privileged-token]
pipeline stage dns 'verify' token: proxmox.read-only-token
pipeline stage dns 'verify' secrets: [proxmox.read-only-token]
pipeline stage dns 'verify-absent' token: proxmox.read-only-token
pipeline stage dns 'verify-absent' secrets: [proxmox.read-only-token]
pipeline stage dns 'verify-current' token: proxmox.read-only-token
pipeline stage dns 'verify-current' secrets: []
pipeline stage dns 'verify-key' token: proxmox.read-only-token
pipeline stage dns 'verify-key' secrets: []
pipeline stage dns 'verify-removal' token: proxmox.read-only-token
pipeline stage dns 'verify-removal' secrets: []
pipeline stage dns 'no-such-step' token: proxmox.read-only-token
pipeline stage dns 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
pipeline stage dns '' token: proxmox.read-only-token
pipeline stage dns '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline stage dns 'Plan' token: proxmox.read-only-token
pipeline stage dns 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
pipeline stage dns 'APPLY' token: proxmox.read-only-token
pipeline stage dns 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
pipeline stage dns ' verify' token: proxmox.read-only-token
pipeline stage dns ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
pipeline stage dns <null> token: proxmox.read-only-token
pipeline stage dns <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline stage deploy 'apply' token: proxmox.privileged-token
pipeline stage deploy 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
pipeline stage deploy 'approval' token: proxmox.read-only-token
pipeline stage deploy 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
pipeline stage deploy 'approve-removal' token: proxmox.read-only-token
pipeline stage deploy 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
pipeline stage deploy 'authorize-key' token: proxmox.read-only-token
pipeline stage deploy 'authorize-key' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.host-key]
pipeline stage deploy 'confirm-refused' token: proxmox.read-only-token
pipeline stage deploy 'confirm-refused' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.host-key]
pipeline stage deploy 'observe' token: proxmox.read-only-token
pipeline stage deploy 'observe' secrets: [proxmox.read-only-token]
pipeline stage deploy 'outcome-approval' token: proxmox.read-only-token
pipeline stage deploy 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
pipeline stage deploy 'plan' token: proxmox.read-only-token
pipeline stage deploy 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
pipeline stage deploy 'playbook' token: proxmox.read-only-token
pipeline stage deploy 'playbook' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.host-key]
pipeline stage deploy 'playbook-approved' token: proxmox.read-only-token
pipeline stage deploy 'playbook-approved' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.host-key]
pipeline stage deploy 'preview' token: proxmox.read-only-token
pipeline stage deploy 'preview' secrets: [ansible.host-key]
pipeline stage deploy 'promote-key' token: proxmox.read-only-token
pipeline stage deploy 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
pipeline stage deploy 'protect' token: proxmox.privileged-token
pipeline stage deploy 'protect' secrets: [proxmox.privileged-token]
pipeline stage deploy 'remove-key' token: proxmox.read-only-token
pipeline stage deploy 'remove-key' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.host-key]
pipeline stage deploy 'reprotect' token: proxmox.privileged-token
pipeline stage deploy 'reprotect' secrets: [proxmox.privileged-token]
pipeline stage deploy 'unprotect' token: proxmox.privileged-token
pipeline stage deploy 'unprotect' secrets: [proxmox.privileged-token]
pipeline stage deploy 'verify' token: proxmox.read-only-token
pipeline stage deploy 'verify' secrets: [proxmox.read-only-token]
pipeline stage deploy 'verify-absent' token: proxmox.read-only-token
pipeline stage deploy 'verify-absent' secrets: [proxmox.read-only-token]
pipeline stage deploy 'verify-current' token: proxmox.read-only-token
pipeline stage deploy 'verify-current' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.host-key]
pipeline stage deploy 'verify-key' token: proxmox.read-only-token
pipeline stage deploy 'verify-key' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.host-key]
pipeline stage deploy 'verify-removal' token: proxmox.read-only-token
pipeline stage deploy 'verify-removal' secrets: [ansible.ssh-key, ansible.var.db_password, ansible.host-key]
pipeline stage deploy 'no-such-step' token: proxmox.read-only-token
pipeline stage deploy 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
pipeline stage deploy '' token: proxmox.read-only-token
pipeline stage deploy '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline stage deploy 'Plan' token: proxmox.read-only-token
pipeline stage deploy 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
pipeline stage deploy 'APPLY' token: proxmox.read-only-token
pipeline stage deploy 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
pipeline stage deploy ' verify' token: proxmox.read-only-token
pipeline stage deploy ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
pipeline stage deploy <null> token: proxmox.read-only-token
pipeline stage deploy <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline managed host key stage dns 'apply' token: proxmox.privileged-token
pipeline managed host key stage dns 'apply' secrets: []
pipeline managed host key stage dns 'approval' token: proxmox.read-only-token
pipeline managed host key stage dns 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
pipeline managed host key stage dns 'approve-removal' token: proxmox.read-only-token
pipeline managed host key stage dns 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
pipeline managed host key stage dns 'authorize-key' token: proxmox.read-only-token
pipeline managed host key stage dns 'authorize-key' secrets: []
pipeline managed host key stage dns 'confirm-refused' token: proxmox.read-only-token
pipeline managed host key stage dns 'confirm-refused' secrets: []
pipeline managed host key stage dns 'observe' token: proxmox.read-only-token
pipeline managed host key stage dns 'observe' secrets: [proxmox.read-only-token]
pipeline managed host key stage dns 'outcome-approval' token: proxmox.read-only-token
pipeline managed host key stage dns 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
pipeline managed host key stage dns 'plan' token: proxmox.read-only-token
pipeline managed host key stage dns 'plan' secrets: []
pipeline managed host key stage dns 'playbook' token: proxmox.read-only-token
pipeline managed host key stage dns 'playbook' secrets: []
pipeline managed host key stage dns 'playbook-approved' token: proxmox.read-only-token
pipeline managed host key stage dns 'playbook-approved' secrets: []
pipeline managed host key stage dns 'preview' token: proxmox.read-only-token
pipeline managed host key stage dns 'preview' secrets: []
pipeline managed host key stage dns 'promote-key' token: proxmox.read-only-token
pipeline managed host key stage dns 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
pipeline managed host key stage dns 'protect' token: proxmox.privileged-token
pipeline managed host key stage dns 'protect' secrets: [proxmox.privileged-token]
pipeline managed host key stage dns 'remove-key' token: proxmox.read-only-token
pipeline managed host key stage dns 'remove-key' secrets: []
pipeline managed host key stage dns 'reprotect' token: proxmox.privileged-token
pipeline managed host key stage dns 'reprotect' secrets: [proxmox.privileged-token]
pipeline managed host key stage dns 'unprotect' token: proxmox.privileged-token
pipeline managed host key stage dns 'unprotect' secrets: [proxmox.privileged-token]
pipeline managed host key stage dns 'verify' token: proxmox.read-only-token
pipeline managed host key stage dns 'verify' secrets: [proxmox.read-only-token]
pipeline managed host key stage dns 'verify-absent' token: proxmox.read-only-token
pipeline managed host key stage dns 'verify-absent' secrets: [proxmox.read-only-token]
pipeline managed host key stage dns 'verify-current' token: proxmox.read-only-token
pipeline managed host key stage dns 'verify-current' secrets: []
pipeline managed host key stage dns 'verify-key' token: proxmox.read-only-token
pipeline managed host key stage dns 'verify-key' secrets: []
pipeline managed host key stage dns 'verify-removal' token: proxmox.read-only-token
pipeline managed host key stage dns 'verify-removal' secrets: []
pipeline managed host key stage dns 'no-such-step' token: proxmox.read-only-token
pipeline managed host key stage dns 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
pipeline managed host key stage dns '' token: proxmox.read-only-token
pipeline managed host key stage dns '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline managed host key stage dns 'Plan' token: proxmox.read-only-token
pipeline managed host key stage dns 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
pipeline managed host key stage dns 'APPLY' token: proxmox.read-only-token
pipeline managed host key stage dns 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
pipeline managed host key stage dns ' verify' token: proxmox.read-only-token
pipeline managed host key stage dns ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
pipeline managed host key stage dns <null> token: proxmox.read-only-token
pipeline managed host key stage dns <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline managed host key stage deploy 'apply' token: proxmox.privileged-token
pipeline managed host key stage deploy 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
pipeline managed host key stage deploy 'approval' token: proxmox.read-only-token
pipeline managed host key stage deploy 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
pipeline managed host key stage deploy 'approve-removal' token: proxmox.read-only-token
pipeline managed host key stage deploy 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
pipeline managed host key stage deploy 'authorize-key' token: proxmox.read-only-token
pipeline managed host key stage deploy 'authorize-key' secrets: [ansible.ssh-key, ansible.var.db_password]
pipeline managed host key stage deploy 'confirm-refused' token: proxmox.read-only-token
pipeline managed host key stage deploy 'confirm-refused' secrets: [ansible.ssh-key, ansible.var.db_password]
pipeline managed host key stage deploy 'observe' token: proxmox.read-only-token
pipeline managed host key stage deploy 'observe' secrets: [proxmox.read-only-token]
pipeline managed host key stage deploy 'outcome-approval' token: proxmox.read-only-token
pipeline managed host key stage deploy 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
pipeline managed host key stage deploy 'plan' token: proxmox.read-only-token
pipeline managed host key stage deploy 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
pipeline managed host key stage deploy 'playbook' token: proxmox.read-only-token
pipeline managed host key stage deploy 'playbook' secrets: [ansible.ssh-key, ansible.var.db_password]
pipeline managed host key stage deploy 'playbook-approved' token: proxmox.read-only-token
pipeline managed host key stage deploy 'playbook-approved' secrets: [ansible.ssh-key, ansible.var.db_password]
pipeline managed host key stage deploy 'preview' token: proxmox.read-only-token
pipeline managed host key stage deploy 'preview' secrets: []
pipeline managed host key stage deploy 'promote-key' token: proxmox.read-only-token
pipeline managed host key stage deploy 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
pipeline managed host key stage deploy 'protect' token: proxmox.privileged-token
pipeline managed host key stage deploy 'protect' secrets: [proxmox.privileged-token]
pipeline managed host key stage deploy 'remove-key' token: proxmox.read-only-token
pipeline managed host key stage deploy 'remove-key' secrets: [ansible.ssh-key, ansible.var.db_password]
pipeline managed host key stage deploy 'reprotect' token: proxmox.privileged-token
pipeline managed host key stage deploy 'reprotect' secrets: [proxmox.privileged-token]
pipeline managed host key stage deploy 'unprotect' token: proxmox.privileged-token
pipeline managed host key stage deploy 'unprotect' secrets: [proxmox.privileged-token]
pipeline managed host key stage deploy 'verify' token: proxmox.read-only-token
pipeline managed host key stage deploy 'verify' secrets: [proxmox.read-only-token]
pipeline managed host key stage deploy 'verify-absent' token: proxmox.read-only-token
pipeline managed host key stage deploy 'verify-absent' secrets: [proxmox.read-only-token]
pipeline managed host key stage deploy 'verify-current' token: proxmox.read-only-token
pipeline managed host key stage deploy 'verify-current' secrets: [ansible.ssh-key, ansible.var.db_password]
pipeline managed host key stage deploy 'verify-key' token: proxmox.read-only-token
pipeline managed host key stage deploy 'verify-key' secrets: [ansible.ssh-key, ansible.var.db_password]
pipeline managed host key stage deploy 'verify-removal' token: proxmox.read-only-token
pipeline managed host key stage deploy 'verify-removal' secrets: [ansible.ssh-key, ansible.var.db_password]
pipeline managed host key stage deploy 'no-such-step' token: proxmox.read-only-token
pipeline managed host key stage deploy 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
pipeline managed host key stage deploy '' token: proxmox.read-only-token
pipeline managed host key stage deploy '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline managed host key stage deploy 'Plan' token: proxmox.read-only-token
pipeline managed host key stage deploy 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
pipeline managed host key stage deploy 'APPLY' token: proxmox.read-only-token
pipeline managed host key stage deploy 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
pipeline managed host key stage deploy ' verify' token: proxmox.read-only-token
pipeline managed host key stage deploy ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
pipeline managed host key stage deploy <null> token: proxmox.read-only-token
pipeline managed host key stage deploy <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
observation split 'apply' token: proxmox.privileged-token
observation split 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
observation split 'approval' token: proxmox.read-only-token
observation split 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
observation split 'approve-removal' token: proxmox.read-only-token
observation split 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
observation split 'authorize-key' token: proxmox.read-only-token
observation split 'authorize-key' secrets: [proxmox.read-only-token, proxmox.ca]
observation split 'confirm-refused' token: proxmox.read-only-token
observation split 'confirm-refused' secrets: [proxmox.read-only-token, proxmox.ca]
observation split 'observe' token: proxmox.read-only-token
observation split 'observe' secrets: [proxmox.read-only-token, proxmox.ca]
observation split 'outcome-approval' token: proxmox.read-only-token
observation split 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
observation split 'plan' token: proxmox.read-only-token
observation split 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
observation split 'playbook' token: proxmox.read-only-token
observation split 'playbook' secrets: [proxmox.read-only-token, proxmox.ca]
observation split 'playbook-approved' token: proxmox.read-only-token
observation split 'playbook-approved' secrets: [proxmox.read-only-token, proxmox.ca]
observation split 'preview' token: proxmox.read-only-token
observation split 'preview' secrets: [ansible.host-key]
observation split 'promote-key' token: proxmox.read-only-token
observation split 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
observation split 'protect' token: proxmox.privileged-token
observation split 'protect' secrets: [proxmox.privileged-token, proxmox.ca]
observation split 'remove-key' token: proxmox.read-only-token
observation split 'remove-key' secrets: [proxmox.read-only-token, proxmox.ca]
observation split 'reprotect' token: proxmox.privileged-token
observation split 'reprotect' secrets: [proxmox.privileged-token, proxmox.ca]
observation split 'unprotect' token: proxmox.privileged-token
observation split 'unprotect' secrets: [proxmox.privileged-token, proxmox.ca]
observation split 'verify' token: proxmox.read-only-token
observation split 'verify' secrets: [proxmox.read-only-token, proxmox.ca]
observation split 'verify-absent' token: proxmox.read-only-token
observation split 'verify-absent' secrets: [proxmox.read-only-token, proxmox.ca]
observation split 'verify-current' token: proxmox.read-only-token
observation split 'verify-current' secrets: [proxmox.read-only-token, proxmox.ca]
observation split 'verify-key' token: proxmox.read-only-token
observation split 'verify-key' secrets: [proxmox.read-only-token, proxmox.ca]
observation split 'verify-removal' token: proxmox.read-only-token
observation split 'verify-removal' secrets: [proxmox.read-only-token, proxmox.ca]
observation split 'no-such-step' token: proxmox.read-only-token
observation split 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
observation split '' token: proxmox.read-only-token
observation split '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
observation split 'Plan' token: proxmox.read-only-token
observation split 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
observation split 'APPLY' token: proxmox.read-only-token
observation split 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
observation split ' verify' token: proxmox.read-only-token
observation split ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
observation split <null> token: proxmox.read-only-token
observation split <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
observation shared without ca 'apply' token: proxmox.shared-token
observation shared without ca 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
observation shared without ca 'approval' token: proxmox.shared-token
observation shared without ca 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
observation shared without ca 'approve-removal' token: proxmox.shared-token
observation shared without ca 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
observation shared without ca 'authorize-key' token: proxmox.shared-token
observation shared without ca 'authorize-key' secrets: [proxmox.shared-token]
observation shared without ca 'confirm-refused' token: proxmox.shared-token
observation shared without ca 'confirm-refused' secrets: [proxmox.shared-token]
observation shared without ca 'observe' token: proxmox.shared-token
observation shared without ca 'observe' secrets: [proxmox.shared-token]
observation shared without ca 'outcome-approval' token: proxmox.shared-token
observation shared without ca 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
observation shared without ca 'plan' token: proxmox.shared-token
observation shared without ca 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
observation shared without ca 'playbook' token: proxmox.shared-token
observation shared without ca 'playbook' secrets: [proxmox.shared-token]
observation shared without ca 'playbook-approved' token: proxmox.shared-token
observation shared without ca 'playbook-approved' secrets: [proxmox.shared-token]
observation shared without ca 'preview' token: proxmox.shared-token
observation shared without ca 'preview' secrets: [ansible.host-key]
observation shared without ca 'promote-key' token: proxmox.shared-token
observation shared without ca 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
observation shared without ca 'protect' token: proxmox.shared-token
observation shared without ca 'protect' secrets: [proxmox.shared-token]
observation shared without ca 'remove-key' token: proxmox.shared-token
observation shared without ca 'remove-key' secrets: [proxmox.shared-token]
observation shared without ca 'reprotect' token: proxmox.shared-token
observation shared without ca 'reprotect' secrets: [proxmox.shared-token]
observation shared without ca 'unprotect' token: proxmox.shared-token
observation shared without ca 'unprotect' secrets: [proxmox.shared-token]
observation shared without ca 'verify' token: proxmox.shared-token
observation shared without ca 'verify' secrets: [proxmox.shared-token]
observation shared without ca 'verify-absent' token: proxmox.shared-token
observation shared without ca 'verify-absent' secrets: [proxmox.shared-token]
observation shared without ca 'verify-current' token: proxmox.shared-token
observation shared without ca 'verify-current' secrets: [proxmox.shared-token]
observation shared without ca 'verify-key' token: proxmox.shared-token
observation shared without ca 'verify-key' secrets: [proxmox.shared-token]
observation shared without ca 'verify-removal' token: proxmox.shared-token
observation shared without ca 'verify-removal' secrets: [proxmox.shared-token]
observation shared without ca 'no-such-step' token: proxmox.shared-token
observation shared without ca 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
observation shared without ca '' token: proxmox.shared-token
observation shared without ca '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
observation shared without ca 'Plan' token: proxmox.shared-token
observation shared without ca 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
observation shared without ca 'APPLY' token: proxmox.shared-token
observation shared without ca 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
observation shared without ca ' verify' token: proxmox.shared-token
observation shared without ca ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
observation shared without ca <null> token: proxmox.shared-token
observation shared without ca <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
empty 'apply' token: proxmox.privileged-token
empty 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
empty 'approval' token: proxmox.read-only-token
empty 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
empty 'approve-removal' token: proxmox.read-only-token
empty 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
empty 'authorize-key' token: proxmox.read-only-token
empty 'authorize-key' secrets: []
empty 'confirm-refused' token: proxmox.read-only-token
empty 'confirm-refused' secrets: []
empty 'observe' token: proxmox.read-only-token
empty 'observe' secrets: [proxmox.read-only-token]
empty 'outcome-approval' token: proxmox.read-only-token
empty 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
empty 'plan' token: proxmox.read-only-token
empty 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
empty 'playbook' token: proxmox.read-only-token
empty 'playbook' secrets: []
empty 'playbook-approved' token: proxmox.read-only-token
empty 'playbook-approved' secrets: []
empty 'preview' token: proxmox.read-only-token
empty 'preview' secrets: [ansible.host-key]
empty 'promote-key' token: proxmox.read-only-token
empty 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
empty 'protect' token: proxmox.privileged-token
empty 'protect' secrets: [proxmox.privileged-token]
empty 'remove-key' token: proxmox.read-only-token
empty 'remove-key' secrets: []
empty 'reprotect' token: proxmox.privileged-token
empty 'reprotect' secrets: [proxmox.privileged-token]
empty 'unprotect' token: proxmox.privileged-token
empty 'unprotect' secrets: [proxmox.privileged-token]
empty 'verify' token: proxmox.read-only-token
empty 'verify' secrets: [proxmox.read-only-token]
empty 'verify-absent' token: proxmox.read-only-token
empty 'verify-absent' secrets: [proxmox.read-only-token]
empty 'verify-current' token: proxmox.read-only-token
empty 'verify-current' secrets: []
empty 'verify-key' token: proxmox.read-only-token
empty 'verify-key' secrets: []
empty 'verify-removal' token: proxmox.read-only-token
empty 'verify-removal' secrets: []
empty 'no-such-step' token: proxmox.read-only-token
empty 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
empty '' token: proxmox.read-only-token
empty '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
empty 'Plan' token: proxmox.read-only-token
empty 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
empty 'APPLY' token: proxmox.read-only-token
empty 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
empty ' verify' token: proxmox.read-only-token
empty ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
empty <null> token: proxmox.read-only-token
empty <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline step of dns 'apply' secrets: []
pipeline step of dns 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
pipeline step of dns 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
pipeline step of dns 'authorize-key' secrets: []
pipeline step of dns 'confirm-refused' secrets: []
pipeline step of dns 'observe' secrets: [dns/proxmox.read-only-token]
pipeline step of dns 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
pipeline step of dns 'plan' secrets: []
pipeline step of dns 'playbook' secrets: []
pipeline step of dns 'playbook-approved' secrets: []
pipeline step of dns 'preview' secrets: [dns/ansible.host-key]
pipeline step of dns 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
pipeline step of dns 'protect' secrets: [dns/proxmox.privileged-token]
pipeline step of dns 'remove-key' secrets: []
pipeline step of dns 'reprotect' secrets: [dns/proxmox.privileged-token]
pipeline step of dns 'unprotect' secrets: [dns/proxmox.privileged-token]
pipeline step of dns 'verify' secrets: [dns/proxmox.read-only-token]
pipeline step of dns 'verify-absent' secrets: [dns/proxmox.read-only-token]
pipeline step of dns 'verify-current' secrets: []
pipeline step of dns 'verify-key' secrets: []
pipeline step of dns 'verify-removal' secrets: []
pipeline step of dns 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
pipeline step of dns '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline step of dns 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
pipeline step of dns 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
pipeline step of dns ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
pipeline step of dns <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline step of deploy 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
pipeline step of deploy 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
pipeline step of deploy 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
pipeline step of deploy 'authorize-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline step of deploy 'confirm-refused' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline step of deploy 'observe' secrets: [deploy/proxmox.read-only-token]
pipeline step of deploy 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
pipeline step of deploy 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
pipeline step of deploy 'playbook' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline step of deploy 'playbook-approved' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline step of deploy 'preview' secrets: [deploy/ansible.host-key]
pipeline step of deploy 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
pipeline step of deploy 'protect' secrets: [deploy/proxmox.privileged-token]
pipeline step of deploy 'remove-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline step of deploy 'reprotect' secrets: [deploy/proxmox.privileged-token]
pipeline step of deploy 'unprotect' secrets: [deploy/proxmox.privileged-token]
pipeline step of deploy 'verify' secrets: [deploy/proxmox.read-only-token]
pipeline step of deploy 'verify-absent' secrets: [deploy/proxmox.read-only-token]
pipeline step of deploy 'verify-current' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline step of deploy 'verify-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline step of deploy 'verify-removal' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline step of deploy 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
pipeline step of deploy '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline step of deploy 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
pipeline step of deploy 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
pipeline step of deploy ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
pipeline step of deploy <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline step of no stage 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
pipeline step of no stage 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
pipeline step of no stage 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
pipeline step of no stage 'authorize-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline step of no stage 'confirm-refused' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline step of no stage 'observe' secrets: [proxmox.read-only-token]
pipeline step of no stage 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
pipeline step of no stage 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
pipeline step of no stage 'playbook' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline step of no stage 'playbook-approved' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline step of no stage 'preview' secrets: [ansible.host-key]
pipeline step of no stage 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
pipeline step of no stage 'protect' secrets: [proxmox.privileged-token]
pipeline step of no stage 'remove-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline step of no stage 'reprotect' secrets: [proxmox.privileged-token]
pipeline step of no stage 'unprotect' secrets: [proxmox.privileged-token]
pipeline step of no stage 'verify' secrets: [proxmox.read-only-token]
pipeline step of no stage 'verify-absent' secrets: [proxmox.read-only-token]
pipeline step of no stage 'verify-current' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline step of no stage 'verify-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline step of no stage 'verify-removal' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password, deploy/ansible.host-key]
pipeline step of no stage 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
pipeline step of no stage '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline step of no stage 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
pipeline step of no stage 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
pipeline step of no stage ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
pipeline step of no stage <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline managed host key step of dns 'apply' secrets: []
pipeline managed host key step of dns 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
pipeline managed host key step of dns 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
pipeline managed host key step of dns 'authorize-key' secrets: []
pipeline managed host key step of dns 'confirm-refused' secrets: []
pipeline managed host key step of dns 'observe' secrets: [dns/proxmox.read-only-token]
pipeline managed host key step of dns 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
pipeline managed host key step of dns 'plan' secrets: []
pipeline managed host key step of dns 'playbook' secrets: []
pipeline managed host key step of dns 'playbook-approved' secrets: []
pipeline managed host key step of dns 'preview' secrets: []
pipeline managed host key step of dns 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
pipeline managed host key step of dns 'protect' secrets: [dns/proxmox.privileged-token]
pipeline managed host key step of dns 'remove-key' secrets: []
pipeline managed host key step of dns 'reprotect' secrets: [dns/proxmox.privileged-token]
pipeline managed host key step of dns 'unprotect' secrets: [dns/proxmox.privileged-token]
pipeline managed host key step of dns 'verify' secrets: [dns/proxmox.read-only-token]
pipeline managed host key step of dns 'verify-absent' secrets: [dns/proxmox.read-only-token]
pipeline managed host key step of dns 'verify-current' secrets: []
pipeline managed host key step of dns 'verify-key' secrets: []
pipeline managed host key step of dns 'verify-removal' secrets: []
pipeline managed host key step of dns 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
pipeline managed host key step of dns '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline managed host key step of dns 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
pipeline managed host key step of dns 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
pipeline managed host key step of dns ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
pipeline managed host key step of dns <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline managed host key step of deploy 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
pipeline managed host key step of deploy 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
pipeline managed host key step of deploy 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
pipeline managed host key step of deploy 'authorize-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key step of deploy 'confirm-refused' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key step of deploy 'observe' secrets: [deploy/proxmox.read-only-token]
pipeline managed host key step of deploy 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
pipeline managed host key step of deploy 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
pipeline managed host key step of deploy 'playbook' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key step of deploy 'playbook-approved' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key step of deploy 'preview' secrets: []
pipeline managed host key step of deploy 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
pipeline managed host key step of deploy 'protect' secrets: [deploy/proxmox.privileged-token]
pipeline managed host key step of deploy 'remove-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key step of deploy 'reprotect' secrets: [deploy/proxmox.privileged-token]
pipeline managed host key step of deploy 'unprotect' secrets: [deploy/proxmox.privileged-token]
pipeline managed host key step of deploy 'verify' secrets: [deploy/proxmox.read-only-token]
pipeline managed host key step of deploy 'verify-absent' secrets: [deploy/proxmox.read-only-token]
pipeline managed host key step of deploy 'verify-current' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key step of deploy 'verify-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key step of deploy 'verify-removal' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key step of deploy 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
pipeline managed host key step of deploy '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline managed host key step of deploy 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
pipeline managed host key step of deploy 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
pipeline managed host key step of deploy ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
pipeline managed host key step of deploy <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline managed host key step of no stage 'apply' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
pipeline managed host key step of no stage 'approval' secrets: throws InvalidOperationException: The step 'approval' does not run on an agent.
pipeline managed host key step of no stage 'approve-removal' secrets: throws InvalidOperationException: The step 'approve-removal' does not run on an agent.
pipeline managed host key step of no stage 'authorize-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key step of no stage 'confirm-refused' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key step of no stage 'observe' secrets: [proxmox.read-only-token]
pipeline managed host key step of no stage 'outcome-approval' secrets: throws InvalidOperationException: The step 'outcome-approval' does not run on an agent.
pipeline managed host key step of no stage 'plan' secrets: throws NullReferenceException: Object reference not set to an instance of an object.
pipeline managed host key step of no stage 'playbook' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key step of no stage 'playbook-approved' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key step of no stage 'preview' secrets: []
pipeline managed host key step of no stage 'promote-key' secrets: throws InvalidOperationException: The step 'promote-key' does not run on an agent.
pipeline managed host key step of no stage 'protect' secrets: [proxmox.privileged-token]
pipeline managed host key step of no stage 'remove-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key step of no stage 'reprotect' secrets: [proxmox.privileged-token]
pipeline managed host key step of no stage 'unprotect' secrets: [proxmox.privileged-token]
pipeline managed host key step of no stage 'verify' secrets: [proxmox.read-only-token]
pipeline managed host key step of no stage 'verify-absent' secrets: [proxmox.read-only-token]
pipeline managed host key step of no stage 'verify-current' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key step of no stage 'verify-key' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key step of no stage 'verify-removal' secrets: [deploy/ansible.ssh-key, deploy/ansible.var.db_password]
pipeline managed host key step of no stage 'no-such-step' secrets: throws InvalidOperationException: The step 'no-such-step' does not run on an agent.
pipeline managed host key step of no stage '' secrets: throws InvalidOperationException: The step '' does not run on an agent.
pipeline managed host key step of no stage 'Plan' secrets: throws InvalidOperationException: The step 'Plan' does not run on an agent.
pipeline managed host key step of no stage 'APPLY' secrets: throws InvalidOperationException: The step 'APPLY' does not run on an agent.
pipeline managed host key step of no stage ' verify' secrets: throws InvalidOperationException: The step ' verify' does not run on an agent.
pipeline managed host key step of no stage <null> secrets: throws InvalidOperationException: The step '' does not run on an agent.
""";
}
