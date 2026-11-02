using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Octopushy.Definitions;
using Octopushy.Server.Data;
using Octopushy.Server.Options;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Definitions;

/// <summary>The ha-db example: a container recipe built on lxc-app, with two protected secrets and a guest of its own.</summary>
public class HaDbRecipeTests
{
    private const string Guest = "home/ziost/ha-db";
    private const string GuestFile = "labs/home/proxmox/ziost/guests/ha-db/guest.yaml";

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

    private static List<string> Errors(TestRepository test) =>
        LabRepositoryLoader.Load(test.Root).Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message).ToList();

    private static Run Build(LabRepository repository, GuestDefinition guest) =>
        new RunFactory(Options.Create(new OctopushyOptions()), TimeProvider.System).Guest(repository, guest,
            new Resource { Kind = ResourceKinds.Guest, Address = guest.Address, Lab = guest.Lab },
            new DefinitionRevision { Commit = "rev" }, "test");

    [Fact]
    public void The_example_lab_loads_without_an_issue_and_ha_db_is_a_container_on_ziost()
    {
        var repository = LabRepositoryLoader.Load(TestRepository.ExampleLab);
        Assert.Empty(repository.Issues);
        var guest = repository.Guest(Guest)!;
        Assert.True(guest.IsContainer);
        Assert.Equal(("ziost", 102, "ha-db"), (guest.Host, guest.Vmid, guest.Recipe));
        Assert.Equal("manual", guest.Policy!.Destructive);
        Assert.Equal(new StoreReference("ha-db/postgres-password"), guest.ProvisionSecrets["postgres_password"]);
        Assert.Equal(new StoreReference("ha-db/victoriametrics-password"), guest.ProvisionSecrets["victoriametrics_password"]);
        Assert.Equal(["postgres_password", "victoriametrics_password"], repository.Recipes["ha-db"].Secrets!.Order());
        Assert.Null(repository.ClusterOf(Guest));
    }

    [Fact]
    public void Its_run_hands_the_roots_the_hardware_network_and_startup_the_guest_declares()
    {
        var repository = LabRepositoryLoader.Load(TestRepository.ExampleLab);
        var run = Build(repository, repository.Guest(Guest)!);
        var context = RunContext.Parse(run.Context);
        var guest = context.OpenTofu!.Variables["guest"]!;
        Assert.Equal(("ziost", 102), (guest["host"]!.GetValue<string>(), guest["vmid"]!.GetValue<int>()));
        Assert.Equal((2, 1024, 256), (guest["hardware"]!["cores"]!.GetValue<int>(), guest["hardware"]!["memory"]!.GetValue<int>(), guest["hardware"]!["swap"]!.GetValue<int>()));
        var disk = Assert.Single(guest["hardware"]!["disks"]!.AsArray());
        Assert.Equal((32, "vm-ssd"), (disk!["size"]!.GetValue<int>(), disk["datastore"]!.GetValue<string>()));
        Assert.Equal(("apps", "static", "10.15.30.40/24", "10.15.30.1"),
            (guest["network"]!["bridge"]!.GetValue<string>(), guest["network"]!["mode"]!.GetValue<string>(),
                guest["network"]!["address"]!.GetValue<string>(), guest["network"]!["gateway"]!.GetValue<string>()));
        Assert.Equal((1, 30), (guest["inputs"]!["startup"]!["order"]!.GetValue<int>(), guest["inputs"]!["startup"]!["upDelay"]!.GetValue<int>()));
        Assert.Equal("10.15.40.10", guest["inputs"]!["postgresql"]!["clients"]![0]!.GetValue<string>());
        Assert.Equal("lxc", context.Proxmox.GuestKind);
        Assert.Equal("recipe.TF_VAR_postgres_password", context.OpenTofu.SecretEnv["TF_VAR_postgres_password"]);
        Assert.Equal("ha-db/victoriametrics-password", context.Secrets["recipe.TF_VAR_victoriametrics_password"]);
        Assert.Equal("ziost", context.Proxmox.Node);
        // The datastore and the bridge are declared by the installation the guest sits under.
        Assert.Equal("vm-ssd", context.OpenTofu.Variables["host"]!["datastores"]!["disks"]!.GetValue<string>());
        Assert.Equal("apps", context.OpenTofu.Variables["host"]!["bridges"]![0]!.GetValue<string>());
    }

    [Fact]
    public void What_the_root_calls_is_listed_so_a_change_to_it_reaches_the_guests_digest()
    {
        var shipped = LabRepositoryLoader.Load(TestRepository.ExampleLab);
        var before = ReleaseBinding.Of(shipped, shipped.Guest(Guest)!).Digest;
        // A run that holds a protected secret is given exactly these folders and no others.
        Assert.Equal(["recipes/ha-db", "modules/opentofu/ha-db", "modules/opentofu/lxc-app", "modules/opentofu/proxmox-lxc"],
            RunContext.Parse(Build(shipped, shipped.Guest(Guest)!).Context).ReviewedPaths);
        foreach (var file in new[] { "modules/opentofu/lxc-app/guest/run.sh", "modules/opentofu/proxmox-lxc/main.tf", "modules/opentofu/ha-db/files/install.sh", "modules/opentofu/ha-db/main.tf" })
        {
            using var test = Example();
            File.AppendAllText(Path.Combine(test.Root, file), "\n# changed\n");
            var repository = LabRepositoryLoader.Load(test.Root);
            Assert.NotEqual(before, ReleaseBinding.Of(repository, repository.Guest(Guest)!).Digest);
        }
    }

    [Fact]
    public void A_module_the_recipe_does_not_list_cannot_be_reviewed()
    {
        using var test = Example();
        var recipe = Path.Combine(test.Root, "recipes/ha-db/recipe.yaml");
        File.WriteAllText(recipe, File.ReadAllText(recipe).Replace("modules: [../../modules/opentofu/lxc-app, ../../modules/opentofu/proxmox-lxc]", "modules: [../../modules/opentofu/lxc-app]"));
        Assert.Contains(Errors(test), e => e.Contains("calls a module from ../proxmox-lxc"));

        File.WriteAllText(recipe, File.ReadAllText(recipe).Replace("modules: [../../modules/opentofu/lxc-app]", ""));
        Assert.Contains(Errors(test), e => e.Contains("calls a module from ../lxc-app"));
    }

    [Fact]
    public void A_root_without_a_lock_file_cannot_be_reviewed()
    {
        using var test = Example();
        File.Delete(Path.Combine(test.Root, "modules/opentofu/ha-db/.terraform.lock.hcl"));
        Assert.Contains(Errors(test), e => e.Contains(".terraform.lock.hcl"));
    }

    [Theory]
    [InlineData("provider \"x\" { token = \"$${var.x}\" }")]
    [InlineData("locals { x = \"%%{ if true }\" }")]
    [InlineData("locals { x = \"${ upper(\"a\") /* hidden */ }\" }")]
    public void The_review_scanner_refuses_what_it_cannot_read_with_certainty(string code)
    {
        using var test = Example();
        File.WriteAllText(Path.Combine(test.Root, "modules/opentofu/ha-db/extra.tf"), code);
        Assert.Contains(Errors(test), e => e.Contains("extra.tf cannot be read with certainty"));
    }

    [Fact]
    public void A_form_of_code_the_scanner_does_not_read_is_refused()
    {
        using var test = Example();
        File.WriteAllText(Path.Combine(test.Root, "modules/opentofu/ha-db/extra.tofu"), "# x");
        Assert.Contains(Errors(test), e => e.Contains("extra.tofu is a form of OpenTofu code that Octopushy does not read"));
    }

    [Fact]
    public void A_guest_that_leaves_a_secret_out_is_invalid()
    {
        using var test = Example();
        var guest = Path.Combine(test.Root, GuestFile);
        File.WriteAllText(guest, File.ReadAllText(guest).Replace("    victoriametrics_password: { store: ha-db/victoriametrics-password }\n", ""));
        Assert.Contains(Errors(test), e => e.Contains("needs provision.secrets.victoriametrics_password"));
    }

    [Theory]
    [InlineData("network: { bridge: apps, mode: dhcp }", "network")]
    [InlineData("clients: [10.15.40.0/24]", "clients")]
    [InlineData("retention: 2 years", "retention")]
    [InlineData("sha256: nothex", "sha256")]
    public void The_recipe_schema_refuses_inputs_the_root_could_not_use(string change, string where)
    {
        using var test = Example();
        var guest = Path.Combine(test.Root, GuestFile);
        var text = File.ReadAllText(guest);
        var changed = where switch
        {
            "network" => text.Replace("network:\n  bridge: apps\n  mode: static\n  address: 10.15.30.40/24\n  gateway: 10.15.30.1", change),
            "clients" => text.Replace("clients: [10.15.40.10]", change),
            "retention" => text.Replace("retention: 2y", change),
            _ => text.Replace("sha256: 7180e65913d55ab99c0be8f4a3ad17a0fafe5598a21ff6327fecba3190d7b176", change),
        };
        Assert.NotEqual(text, changed);
        File.WriteAllText(guest, changed);
        Assert.NotEmpty(Errors(test));
    }

    [Fact]
    public void A_mount_point_is_refused_because_it_would_not_outlive_the_container()
    {
        using var test = Example();
        var guest = Path.Combine(test.Root, GuestFile);
        File.WriteAllText(guest, File.ReadAllText(guest).Replace("hardware:\n", "hardware:\n  mountPoints:\n    - { path: /data, size: 8 }\n"));
        Assert.NotEmpty(Errors(test));
    }
}
