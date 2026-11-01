using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Definitions;

public class ContainerGuestTests
{
    public const string Guest = "home/korriban/krb-ct-01";
    public const string GuestPath = "labs/home/proxmox/korriban/guests/krb-ct-01/guest.yaml";
    private const string Dns = "labs/home/proxmox/korriban/guests/krb-dns-01/guest.yaml";

    private const string Definition = """
        schemaVersion: 1
        type: container
        host: korriban
        vmid: 311
        hardware: { cores: 1, memory: 512 }
        network: { bridge: vmbr0, mode: static, address: 10.1.0.33/24, gateway: 10.1.0.1 }
        provision:
          recipe: test-ct
          state: { backend: postgres }
        """;

    /// <summary><see cref="TestRepository.Minimal"/> with a container guest and a recipe for it.</summary>
    public static TestRepository Repository(string guest = Definition, string recipe = "")
    {
        var test = TestRepository.Minimal();
        test.Write(GuestPath, guest);
        test.Write("modules/opentofu/test-vm/.terraform.lock.hcl", "# lock\n");
        test.Write("recipes/test-ct/recipe.yaml", $$"""
            schemaVersion: 1
            operation: provision
            executor: opentofu
            root: ../../modules/opentofu/test-vm
            verify: proxmox
            guestType: container
            {{recipe}}
            """);
        return test;
    }

    /// <summary>A copy of the example lab, to change one thing in.</summary>
    private static TestRepository Example()
    {
        var test = new TestRepository();
        foreach (var file in Directory.EnumerateFiles(TestRepository.ExampleLab, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(test.Root, Path.GetRelativePath(TestRepository.ExampleLab, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return test;
    }

    private static void Replace(TestRepository test, string path, string from, string to)
    {
        var text = File.ReadAllText(Path.Combine(test.Root, path));
        Assert.Contains(from, text);
        test.Write(path, text.Replace(from, to).TrimEnd('\n'));
    }

    private static List<string> Errors(TestRepository test) =>
        LabRepositoryLoader.Load(test.Root).Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message).ToList();

    private static Run Build(LabRepository repository, GuestDefinition guest, OctopushyOptions? options = null) =>
        new RunFactory(Options.Create(options ?? new OctopushyOptions()), TimeProvider.System).Guest(repository, guest,
            new Resource { Kind = ResourceKinds.Guest, Address = guest.Address, Lab = guest.Lab },
            new DefinitionRevision { Commit = "rev" }, "test");

    private static OctopushyOptions WithReporter() => new()
    {
        DiskReporter = new()
        {
            Enabled = true,
            PublicUrl = "https://octopushy.test",
            BinaryUrl = "https://releases.test/disk-reporter",
            BinarySha256 = new string('a', 64),
        },
    };

    [Fact]
    public void The_example_containers_are_technitium_guests_with_a_store_reference_for_the_password()
    {
        var repository = LabRepositoryLoader.Load(TestRepository.ExampleLab);
        foreach (var address in new[] { "home/korriban/krb-dns-01", "home/moraband/mrb-dns-01" })
        {
            var guest = repository.Guest(address)!;
            Assert.True(guest.IsContainer);
            Assert.Equal(new StoreReference("dns/technitium/admin-password"), guest.ProvisionSecrets["admin_password"]);
            Assert.Contains(repository.StoreUses, u => u.Key == "dns/technitium/admin-password" && u.Consumer == address);
        }
    }

    [Fact]
    public void A_container_runs_the_plain_steps_and_never_a_disk_reporter()
    {
        using var test = Repository();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        var guest = repository.Guest(Guest)!;
        var vm = repository.Guest("home/korriban/krb-app-01")!;

        var run = Build(repository, guest, WithReporter());
        Assert.Equal(["plan", "approval", "unprotect", "apply", "verify"], run.Steps.Select(s => s.Name));
        var context = RunContext.Parse(run.Context);
        Assert.Null(context.DiskReporter);
        Assert.Equal("lxc", context.Proxmox.GuestKind);

        // The same settings install it on a VM.
        Assert.Contains(Build(repository, vm, WithReporter()).Steps, s => s.Name == StepNames.InstallDiskReporter);
        Assert.Null(RunContext.Parse(Build(repository, vm).Context).Proxmox.GuestKind);
    }

    [Fact]
    public void Asking_a_container_for_a_disk_reporter_is_a_warning_and_changes_nothing()
    {
        using var test = Repository(Definition + "\nsetup: { diskReporter: { enabled: true } }\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        var issue = Assert.Single(repository.Issues);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Contains("QEMU guest agent", issue.Message);
        Assert.Equal(5, Build(repository, repository.Guest(Guest)!, WithReporter()).Steps.Count);
    }

    [Fact]
    public void A_worker_section_is_invalid_on_a_container()
    {
        using var test = Repository(Definition + """

            worker:
              templateVersion: 1
              generation: 01234567-89ab-cdef-0123-456789abcdef
              labels: [home]
              maxConcurrency: 1
            """);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(repository.Issues, i => i.Severity == IssueSeverity.Error && i.Address == Guest);
        Assert.Null(repository.Guest(Guest));
    }

    [Fact]
    public void The_guest_and_its_recipe_must_agree_on_the_type()
    {
        using (var container = Repository(Definition.Replace("test-ct", "test-vm")))
            Assert.Contains(Errors(container), e => e.Contains("provisions a vm, not a container"));
        using (var vm = Repository(Definition.Replace("type: container", "type: vm")))
            Assert.Contains(Errors(vm), e => e.Contains("provisions a container, not a vm"));
    }

    [Fact]
    public void Guest_filesystem_growth_does_not_apply_to_containers()
    {
        using var test = Repository(recipe: "guestFilesystem: nixos-ext4-root");
        Assert.Contains(Errors(test), e => e.Contains("does not apply to containers"));
    }

    [Theory]
    [InlineData("disk.img", true)]
    [InlineData("debian-13.tar.zst", true)]
    [InlineData("debian-13.tar.xz", true)]
    [InlineData("debian-13.tar.gz", true)]
    [InlineData("debian-13.tar", false)]
    public void A_template_may_name_a_container_archive(string fileName, bool valid)
    {
        using var test = Repository();
        test.Write("templates/base/template.yaml", $$"""
            schemaVersion: 1
            source:
              url: https://images.test/image
              checksum: sha256:{{new string('a', 64)}}
              fileName: {{fileName}}
            """);
        Assert.Equal(valid, Errors(test).Count == 0);
    }

    [Fact]
    public void Provision_inputs_reach_opentofu_and_the_recipe_schema()
    {
        using var test = Example();
        var repository = LabRepositoryLoader.Load(test.Root);
        var guest = repository.Guest("home/korriban/krb-dns-01")!;
        var variables = ProvisionInputs.Build(repository, guest, "rev").Variables;
        Assert.Equal("15.6.2", variables["guest"]!["inputs"]!["technitium"]!["version"]!.GetValue<string>());
        Assert.Equal(1, variables["guest"]!["inputs"]!["startup"]!["order"]!.GetValue<int>());
        Assert.False(variables["guest"]!.AsObject().ContainsKey("secrets"));
        Assert.DoesNotContain("admin-password", variables.ToJsonString());

        var vm = ProvisionInputs.Build(repository, repository.Guest("home/korriban/krb-app-01")!, "rev").Variables;
        Assert.False(vm["guest"]!.AsObject().ContainsKey("inputs"));
    }

    [Theory]
    [InlineData("15.6.2", true)]
    [InlineData("15.6.3", true)]
    [InlineData("15.6.10", true)]
    [InlineData("15.7", true)]
    [InlineData("15.10.1", true)]
    [InlineData("16.0", true)]
    [InlineData("100.1.2", true)]
    [InlineData("15.6.1", false)]
    [InlineData("15.6", false)]
    [InlineData("15.5.9", false)]
    [InlineData("14.9.9", false)]
    [InlineData("latest", false)]
    public void The_technitium_version_is_at_least_the_one_that_fixes_cluster_sync(string version, bool valid)
    {
        using var test = Example();
        Replace(test, Dns, "version: 15.6.2", $"version: \"{version}\"");
        Assert.Equal(valid, !Errors(test).Any(e => e.Contains("recipe technitium-lxc")));
    }

    [Theory]
    [InlineData("mode: static", "mode: dhcp")]
    [InlineData("    - { path: /etc/dns, size: 4 } # Technitium's configuration and zones\n", "    - { path: /srv/data, size: 4 }\n")]
    [InlineData("  dns: [10.1.0.1]                 # what the container resolves through; never itself\n", "")]
    [InlineData("  dns: [10.1.0.1]", "  dns: []")]
    [InlineData("      aspnetcoreVersion: 10.0.14\n", "")]
    public void The_technitium_recipe_refuses_what_it_cannot_run_with(string from, string to)
    {
        using var test = Example();
        Replace(test, Dns, from, to);
        Assert.Contains(Errors(test), e => e.Contains("recipe technitium-lxc"));
    }

    [Fact]
    public void A_secret_bound_to_a_recipe_that_declares_none_gets_the_same_review()
    {
        using var test = Example();
        File.Delete(Path.Combine(test.Root, "modules/opentofu/proxmox-lxc/.terraform.lock.hcl"));
        Replace(test, Dns, "  recipe: technitium-lxc\n", "  recipe: proxmox-lxc\n");
        Assert.Contains(Errors(test), e => e.Contains("no .terraform.lock.hcl"));
    }

    [Fact]
    public void A_recipe_can_require_its_secrets()
    {
        using var test = Example();
        Replace(test, Dns, "  secrets:\n    admin_password: { store: dns/technitium/admin-password }\n", "");
        Assert.Contains(Errors(test), e => e.Contains("needs provision.secrets.admin_password"));
    }

    [Fact]
    public void A_change_to_a_module_the_recipe_declares_plans_its_guests_again()
    {
        using var test = Example();
        string Hash()
        {
            var repository = LabRepositoryLoader.Load(test.Root);
            return ProvisionInputs.Build(repository, repository.Guest("home/korriban/krb-dns-01")!, "rev").Hash;
        }

        var before = Hash();
        test.Write("modules/opentofu/proxmox-lxc/extra.tf", "# a change");
        Assert.NotEqual(before, Hash());
        Assert.Empty(Errors(test));
    }

    [Fact]
    public void A_recipe_module_must_exist_and_stay_in_the_repository()
    {
        using (var missing = Repository(recipe: "modules: [../../modules/opentofu/absent]"))
            Assert.Contains(Errors(missing), e => e.Contains("does not exist"));
        using (var outside = Repository(recipe: "modules: [../../../outside]"))
            Assert.Contains(Errors(outside), e => e.Contains("stay inside the repository"));
    }

    [Fact]
    public void Provision_secrets_become_variables_and_stay_out_of_the_context_as_values()
    {
        using var test = Repository(Definition + """

              secrets:
                admin_password: { store: dns/admin, version: 2 }
            """);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        var context = RunContext.Parse(Build(repository, repository.Guest(Guest)!).Context);
        Assert.Equal("recipe.TF_VAR_admin_password", context.OpenTofu!.SecretEnv["TF_VAR_admin_password"]);
        Assert.Equal("dns/admin", context.Secrets["recipe.TF_VAR_admin_password"]);
        Assert.Equal(2, context.RequestedSecretVersions["recipe.TF_VAR_admin_password"]);
        Assert.Empty(CredentialBindings.Validate(context));
    }

    [Fact]
    public void A_plan_never_receives_a_provision_secret_and_the_apply_does()
    {
        using var test = Repository(Definition + "\n  secrets:\n    admin_password: { store: dns/admin }\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        var context = RunContext.Parse(Build(repository, repository.Guest(Guest)!).Context);
        Assert.DoesNotContain("recipe.TF_VAR_admin_password", AssignmentBuilder.SecretsFor(StepNames.Plan, context));
        Assert.Contains("recipe.TF_VAR_admin_password", AssignmentBuilder.SecretsFor(StepNames.Apply, context));
    }

    [Fact]
    public void Retiring_needs_no_provision_secret()
    {
        using var test = Repository("lifecycle: retired\n" + Definition + "\n  secrets:\n    admin_password: { store: dns/admin }\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        var run = Build(repository, repository.Guest(Guest)!);
        Assert.Equal(Operations.Retire, run.Operation);
        var context = RunContext.Parse(run.Context);
        Assert.DoesNotContain(context.Secrets.Keys, k => k.StartsWith("recipe.", StringComparison.Ordinal));
        Assert.Equal("lxc", context.Proxmox.GuestKind);
    }

    [Theory]
    [InlineData("guest")]
    [InlineData("octopushy")]
    [InlineData("template")]
    [InlineData("Admin")]
    [InlineData("TF_VAR_x")]
    [InlineData("1x")]
    public void Provision_secret_names_are_plain_variables_that_tfvars_cannot_shadow(string name)
    {
        using var test = Repository(Definition + $"\n  secrets:\n    {name}: {{ store: dns/admin }}\n");
        Assert.NotEmpty(Errors(test));
    }

    [Fact]
    public void Definitions_cannot_hand_a_system_key_or_a_privileged_token_to_a_recipe()
    {
        using (var system = Repository(Definition + "\n  secrets:\n    x: { store: system/agent-ca }\n"))
            Assert.Contains(Errors(system), e => e.Contains("system scope"));
        using (var privileged = Repository(Definition + "\n  secrets:\n    x: { store: proxmox/korriban/privileged-token }\n"))
            Assert.Contains(Errors(privileged), e => e.Contains("privileged Proxmox token"));
    }

    [Fact]
    public void A_recipe_role_only_ever_means_an_opentofu_variable()
    {
        using var test = Repository();
        var repository = LabRepositoryLoader.Load(test.Root);
        var context = RunContext.Parse(Build(repository, repository.Guest(Guest)!).Context);
        context.Secrets["recipe.AWS_SECRET"] = "dns/admin";
        context.OpenTofu!.SecretEnv["AWS_SECRET"] = "recipe.AWS_SECRET";
        Assert.Contains(CredentialBindings.Validate(context), p => p.Contains("AWS_SECRET"));
    }
}
