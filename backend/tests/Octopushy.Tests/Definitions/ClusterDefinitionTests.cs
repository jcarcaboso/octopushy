using Octopushy.Definitions;
using Octopushy.Definitions.Schemas;

namespace Octopushy.Tests.Definitions;

public class ClusterDefinitionTests
{
    public const string Cluster = "home/clusters/technitium";
    public const string ClusterFile = "labs/home/clusters/technitium/cluster.yaml";
    public const string Primary = "home/korriban/krb-dns-01";
    public const string Secondary = "home/moraband/mrb-dns-01";
    public const string PrimaryFile = "labs/home/proxmox/korriban/guests/krb-dns-01/guest.yaml";
    public const string SecondaryFile = "labs/home/proxmox/moraband/guests/mrb-dns-01/guest.yaml";

    public const string Definition = """
        schemaVersion: 1
        type: technitium
        members:
          - { guest: korriban/krb-dns-01, role: primary }
          - { guest: moraband/mrb-dns-01, role: secondary }
        settings:
          clusterDomain: cluster.home.arpa
          recursion: { policy: UseSpecifiedNetworkACL, networks: [10.1.0.0/24] }
          forwardZones:
            - { zone: home.arpa, forwarder: 10.1.0.1 }
          blocking:
            enabled: true
            type: NxDomain
            updateIntervalHours: 24
            lists: [https://lists.example.invalid/list.txt]
            blocked: [bad.example.com]
            allowed: [fine.example.com]
            listProbes: [listed.example.com]
        credentials: { store: dns/admin }
        """;

    public static string Guest(string host, int vmid, string address, string extra = "") => $$"""
        schemaVersion: 1
        type: container
        host: {{host}}
        vmid: {{vmid}}
        hardware: { cores: 1, memory: 512 }
        network: { bridge: vmbr0, mode: static, address: {{address}}/24, gateway: 10.1.0.1 }
        provision:
          recipe: test-ct
          state: { backend: postgres }
          secrets:
            admin_password: { store: dns/admin }
        {{extra}}
        """;

    /// <summary><see cref="TestRepository.Minimal"/> with two containers on two installations, and a cluster of them.</summary>
    public static TestRepository Repository(string cluster = Definition)
    {
        var test = TestRepository.Minimal();
        test.Delete("labs/home/proxmox/korriban/guests/krb-app-01");
        test.Write("labs/home/proxmox/moraband/installation.yaml", """
            schemaVersion: 1
            type: standalone
            connection:
              endpoint: https://10.1.0.20:8006/
              tls: { verify: false }
              readOnlyToken: { store: proxmox/korriban/read-only-token }
              privilegedToken: { store: proxmox/korriban/privileged-token }
            hosts:
              moraband:
                datastores: { images: local, disks: local-lvm }
                bridges: [vmbr0]
            """);
        test.Write("recipes/test-ct/recipe.yaml", """
            schemaVersion: 1
            operation: provision
            executor: opentofu
            root: ../../modules/opentofu/test-vm
            verify: proxmox
            guestType: container
            """);
        test.Write("recipes/technitium-cluster/recipe.yaml", """
            schemaVersion: 1
            operation: cluster
            executor: opentofu
            root: ../../modules/opentofu/cluster
            """);
        test.Write("modules/opentofu/cluster/main.tf", "# a root\n");
        test.Write("modules/opentofu/cluster/.terraform.lock.hcl", "# pinned\n");
        test.Write("modules/opentofu/test-vm/.terraform.lock.hcl", "# pinned\n");
        test.Write(PrimaryFile, Guest("korriban", 320, "10.1.0.32"));
        test.Write(SecondaryFile, Guest("moraband", 420, "10.1.0.42"));
        test.Write(ClusterFile, cluster);
        return test;
    }

    private static List<string> Errors(TestRepository test, string address) =>
        LabRepositoryLoader.Load(test.Root).Issues
            .Where(i => i.Severity == IssueSeverity.Error && i.Address == address).Select(i => i.Message).ToList();

    private static string With(string from, string to)
    {
        Assert.Contains(from, Definition);
        return Definition.Replace(from, to);
    }

    [Fact]
    public void A_valid_cluster_loads_with_its_addresses_and_policy()
    {
        using var test = Repository();
        var repository = LabRepositoryLoader.Load(test.Root);
        var cluster = Assert.Single(repository.Clusters);
        Assert.True(repository.IsValid(Cluster));
        Assert.Equal(Cluster, cluster.Address);
        Assert.Equal("home.clusters.technitium", cluster.Stack);
        Assert.Equal(Primary, cluster.Primary!.Guest);
        Assert.Equal([Primary, Secondary], cluster.Members.Select(m => m.Guest));
        Assert.Same(cluster, repository.ClusterOf(Secondary));
        Assert.Null(repository.ClusterOf("home/korriban/other"));
    }

    [Fact]
    public void The_example_lab_loads_its_cluster()
    {
        var repository = LabRepositoryLoader.Load(TestRepository.ExampleLab);
        var cluster = repository.Cluster(Cluster);
        Assert.NotNull(cluster);
        Assert.True(repository.IsValid(Cluster), string.Join("\n", repository.IssuesFor(Cluster).Select(i => i.Message)));
        var inputs = ClusterInputs.Build(repository, cluster, "abc");
        Assert.Equal(["https://10.1.0.32:53443", "https://10.1.0.42:53443"], inputs.Endpoints);
    }

    [Theory]
    [InlineData("type: technitium", "type: consul")]
    [InlineData("role: secondary", "role: replica")]
    [InlineData("clusterDomain: cluster.home.arpa", "clusterDomain: Not_a_domain")]
    [InlineData("type: NxDomain", "type: Sinkhole")]
    [InlineData("updateIntervalHours: 24", "updateIntervalHours: 0")]
    [InlineData("lists: [https://lists.example.invalid/list.txt]", "lists: [ftp://lists.example.invalid/list.txt]")]
    [InlineData("credentials: { store: dns/admin }", "credentials: { value: hunter2 }")]
    [InlineData("credentials: { store: dns/admin }", "credentials: { store: dns/admin }\ntls: { verify: false }")]
    public void The_schema_refuses(string from, string to)
    {
        using var test = Repository(With(from, to));
        Assert.NotEmpty(Errors(test, Cluster));
        Assert.False(LabRepositoryLoader.Load(test.Root).IsValid(Cluster));
    }

    [Fact]
    public void A_folder_without_a_definition_is_only_a_warning()
    {
        using var test = Repository();
        test.Delete(ClusterFile);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Clusters);
        Assert.Contains(repository.Issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("no cluster.yaml"));
    }

    [Theory]
    [InlineData("role: primary", "role: secondary", "exactly one primary")]
    [InlineData("role: secondary", "role: primary", "exactly one primary")]
    [InlineData("guest: moraband/mrb-dns-01", "guest: moraband/nope", "does not exist")]
    [InlineData("guest: moraband/mrb-dns-01", "guest: office/moraband/mrb-dns-01", "belong to one lab")]
    [InlineData("guest: moraband/mrb-dns-01", "guest: korriban/krb-dns-01", "listed more than once")]
    [InlineData("credentials: { store: dns/admin }", "credentials: { store: dns/other }", "members share one password")]
    [InlineData("credentials: { store: dns/admin }", "credentials: { store: proxmox/korriban/read-only-token }", "Proxmox token")]
    [InlineData("    allowed: [fine.example.com]", "    allowed: [fine.example.com]\n    control: canary.test", ".test name")]
    [InlineData("    - { zone: home.arpa, forwarder: 10.1.0.1 }", "    - { zone: home.arpa, forwarder: 10.1.0.1 }\n    - { zone: home.arpa, forwarder: 10.1.0.2 }", "listed more than once")]
    public void A_cluster_is_refused_when(string from, string to, string expected)
    {
        using var test = Repository(With(from, to));
        Assert.Contains(Errors(test, Cluster), e => e.Contains(expected));
    }

    [Fact]
    public void A_cluster_needs_a_secondary()
    {
        using var test = Repository("""
            schemaVersion: 1
            type: technitium
            members:
              - { guest: korriban/krb-dns-01, role: primary }
              - { guest: moraband/mrb-dns-01, role: primary }
            settings: { clusterDomain: cluster.home.arpa }
            credentials: { store: dns/admin }
            """);
        Assert.Contains(Errors(test, Cluster), e => e.Contains("at least one secondary"));
    }

    [Fact]
    public void A_member_needs_a_static_address()
    {
        using var test = Repository();
        test.Write(SecondaryFile, Guest("moraband", 420, "10.1.0.42").Replace("mode: static, address: 10.1.0.42/24, gateway: 10.1.0.1", "mode: dhcp, reservedAddress: 10.1.0.42"));
        Assert.Contains(Errors(test, Cluster), e => e.Contains("static address"));
    }

    [Fact]
    public void A_guest_belongs_to_one_cluster()
    {
        using var test = Repository();
        test.Write("labs/home/clusters/second/cluster.yaml", Definition);
        Assert.Contains(Errors(test, Cluster), e => e.Contains("belongs to at most one cluster"));
        Assert.Contains(Errors(test, "home/clusters/second"), e => e.Contains("belongs to at most one cluster"));
    }

    [Fact]
    public void Members_on_one_host_are_a_warning_and_still_valid()
    {
        using var test = Repository(With("guest: moraband/mrb-dns-01", "guest: korriban/krb-dns-02"));
        test.Delete("labs/home/proxmox/moraband");
        test.Write("labs/home/proxmox/korriban/guests/krb-dns-02/guest.yaml", Guest("korriban", 321, "10.1.0.33"));
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.True(repository.IsValid(Cluster));
        Assert.Contains(repository.IssuesFor(Cluster), i => i.Severity == IssueSeverity.Warning && i.Message.Contains("one failure domain"));
    }

    [Fact]
    public void A_listed_guest_cannot_be_retired()
    {
        using var test = Repository();
        test.Write(SecondaryFile, Guest("moraband", 420, "10.1.0.42", "lifecycle: retired"));
        Assert.Contains(Errors(test, Secondary), e => e.Contains("remove it from home/clusters/technitium first"));
        Assert.False(LabRepositoryLoader.Load(test.Root).IsValid(Secondary));
    }

    [Fact]
    public void The_password_cannot_also_reach_every_guests_plan()
    {
        using var test = Repository();
        test.Write("octopushy.yaml", """
            schemaVersion: 1
            stateBackends:
              postgres:
                type: pg
                env:
                  PG_CONN_STR: { store: opentofu/pg }
                  LEAK: { store: dns/admin }
            """);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(repository.Issues, i => i.Message.Contains("every guest's plan would receive it"));
        Assert.False(repository.RepositoryValid);
    }

    [Theory]
    [InlineData("main.tf", "module \"other\" {\n  source = \"../other\"\n}\n")]
    [InlineData("main.tf", "/* comment */ module \"outside\" { source = \"../outside\" }\n")]
    [InlineData("main.tf", "/* a\nb */\n\nmodule \"outside\" {\n  source = \"../outside\"\n}\n")]
    [InlineData("main.tf", "locals { a = 1 } module \"outside\" { source = \"../outside\" }\n")]
    [InlineData("main.tf", "module \"remote\" {\n  source = \"git::https://example.invalid/x.git\"\n}\n")]
    [InlineData("main.tf", "module \"dynamic\" {\n  source = var.where\n}\n")]
    [InlineData("main.tf", "/* never closed\nmodule \"outside\" {}\n")]
    [InlineData("extra.tf.json", "{}")]
    [InlineData("extra.tofu", "module \"x\" {}\n")]
    [InlineData("extra.tofu.json", "{}")]
    [InlineData(".terraform/providers/evil", "binary")]
    public void Trust_has_to_cover_all_the_code_in_a_cluster_root(string file, string content)
    {
        using var test = Repository();
        test.Write($"modules/opentofu/cluster/{file}", content);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid(Cluster), string.Join("; ", repository.Issues.Select(i => i.Message)));
    }

    [Fact]
    public void Comments_and_strings_do_not_look_like_module_calls()
    {
        using var test = Repository();
        test.Write("modules/opentofu/cluster/main.tf", """
            # module "not" { source = "../a" }
            // module "not" { source = "../b" }
            /* module "not" { source = "../c" } */
            locals {
              text = "module \"not\" { source = \"../d\" } ${upper("x}")}"
              doc  = <<-EOT
                module "not" { source = "../e" }
              EOT
            }
            """);
        Assert.True(LabRepositoryLoader.Load(test.Root).IsValid(Cluster));
    }

    [Fact]
    public void A_root_needs_a_lock_file_so_that_something_pins_its_providers()
    {
        using var test = Repository();
        test.Delete("modules/opentofu/cluster/.terraform.lock.hcl");
        Assert.False(LabRepositoryLoader.Load(test.Root).IsValid(Cluster));
    }

    [Fact]
    public void A_member_whose_recipe_cannot_be_reviewed_makes_the_cluster_invalid()
    {
        using var test = Repository();
        test.Write("modules/opentofu/test-vm/main.tf", "/* x */ module \"outside\" { source = \"../outside\" }\n");
        Assert.Contains(Errors(test, Cluster), e => e.Contains("cannot be reviewed"));
    }

    [Fact]
    public void A_member_may_use_the_modules_its_recipe_lists()
    {
        var repository = LabRepositoryLoader.Load(TestRepository.ExampleLab);
        Assert.True(repository.IsValid(Cluster), string.Join("\n", repository.IssuesFor(Cluster).Select(i => i.Message)));
    }

    [Theory]
    [InlineData("    listProbes: [listed.example.com]\n", "", "A list needs a name in listProbes")]
    [InlineData("    lists: [https://lists.example.invalid/list.txt]\n", "", "no list is declared")]
    [InlineData("listProbes: [listed.example.com]", "listProbes: [bad.example.com]", "also in blocked")]
    [InlineData("listProbes: [listed.example.com]", "listProbes: [fine.example.com]", "also in allowed")]
    public void A_list_needs_a_probe_that_says_something_about_it(string from, string to, string expected)
    {
        using var test = Repository(With(from, to));
        Assert.Contains(Errors(test, Cluster), e => e.Contains(expected));
    }

    [Fact]
    public void A_cluster_recipe_stands_alone()
    {
        using var test = Repository();
        test.Write("recipes/technitium-cluster/recipe.yaml", """
            schemaVersion: 1
            operation: cluster
            executor: opentofu
            root: ../../modules/opentofu/cluster
            modules: [../../modules/opentofu/test-vm]
            """);
        Assert.Contains(LabRepositoryLoader.Load(test.Root).Issues, i => i.Message.Contains("stands alone"));
    }

    [Fact]
    public void A_provision_recipe_cannot_form_a_cluster()
    {
        using var test = Repository();
        test.Write("recipes/technitium-cluster/recipe.yaml", """
            schemaVersion: 1
            operation: provision
            executor: opentofu
            root: ../../modules/opentofu/cluster
            """);
        Assert.Contains(Errors(test, Cluster), e => e.Contains("does not form clusters"));
    }

    [Fact]
    public void Policy_flows_from_the_lab_and_the_clusters_own_wins()
    {
        using var test = Repository();
        test.Write("labs/home/lab.yaml", """
            schemaVersion: 1
            name: Home
            execution: { agentLabels: [home] }
            policy: { approval: manual }
            """);
        var repository = LabRepositoryLoader.Load(test.Root);
        var policy = PolicyResolver.For(repository, repository.Cluster(Cluster)!);
        Assert.Equal("manual", policy.Approval.Value);
        Assert.Equal("manual", policy.Destructive.Value);
        Assert.Equal(["home"], PolicyResolver.AgentLabels(repository, repository.Cluster(Cluster)!).Value);

        test.Write(ClusterFile, Definition + "\npolicy: { approval: automatic, destructive: manual }\n");
        repository = LabRepositoryLoader.Load(test.Root);
        policy = PolicyResolver.For(repository, repository.Cluster(Cluster)!);
        Assert.Equal("automatic", policy.Approval.Value);
        Assert.Equal(Cluster, policy.Approval.SetBy);
        Assert.Equal("manual", policy.Destructive.Value);
    }

    [Fact]
    public void Inputs_fill_in_addresses_and_leave_no_store_key_in_the_variables()
    {
        using var test = Repository();
        var repository = LabRepositoryLoader.Load(test.Root);
        var inputs = ClusterInputs.Build(repository, repository.Cluster(Cluster)!, "abc");
        var cluster = inputs.Variables["cluster"]!;
        Assert.Equal("10.1.0.32", cluster["primary"]!["address"]!.GetValue<string>());
        Assert.Equal("10.1.0.42", cluster["secondaries"]![Secondary]!["address"]!.GetValue<string>());
        Assert.Equal("Udp", cluster["settings"]!["forwardZones"]![0]!["protocol"]!.GetValue<string>());
        Assert.Equal(["https://10.1.0.32:53443", "https://10.1.0.42:53443"], inputs.Endpoints);
        Assert.DoesNotContain("dns/admin", inputs.Variables.ToJsonString());
    }

    [Fact]
    public void The_root_declares_exactly_what_the_inputs_send()
    {
        var repository = LabRepositoryLoader.Load(TestRepository.ExampleLab);
        var inputs = ClusterInputs.Build(repository, repository.Cluster(Cluster)!, "abc");
        var declared = File.ReadAllText(Path.Combine(TestRepository.ExampleLab, "modules/opentofu/technitium-cluster/variables.tf"));
        void Check(System.Text.Json.Nodes.JsonObject sent)
        {
            foreach (var (name, value) in sent)
            {
                if (name.Contains('/')) { Check(value!.AsObject()); continue; }
                Assert.Matches($@"\b{name}\s+=", declared);
                if (value is System.Text.Json.Nodes.JsonObject child) Check(child);
            }
        }

        Check(inputs.Variables["octopushy"]!.AsObject());
        Check(inputs.Variables["cluster"]!.AsObject());
    }

    [Fact]
    public void The_defaults_of_an_empty_settings_block_are_complete()
    {
        using var test = Repository("""
            schemaVersion: 1
            type: technitium
            members:
              - { guest: korriban/krb-dns-01, role: primary }
              - { guest: moraband/mrb-dns-01, role: secondary }
            settings: { clusterDomain: cluster.home.arpa }
            credentials: { store: dns/admin }
            """);
        var repository = LabRepositoryLoader.Load(test.Root);
        var settings = ClusterInputs.Build(repository, repository.Cluster(Cluster)!, "abc").Variables["cluster"]!["settings"]!;
        Assert.Equal("AllowOnlyForPrivateNetworks", settings["recursion"]!["policy"]!.GetValue<string>());
        Assert.Equal("NxDomain", settings["blocking"]!["type"]!.GetValue<string>());
        Assert.True(settings["blocking"]!["enabled"]!.GetValue<bool>());
        Assert.Empty(settings["blocking"]!["lists"]!.AsArray());
    }

    [Fact]
    public void The_hash_follows_what_a_plan_reads_and_not_policy()
    {
        using var test = Repository();
        var hash = Hash(test);
        var digest = Digest(test);
        test.Write(ClusterFile, Definition + "\npolicy: { approval: manual }\ndescription: a note\n");
        Assert.Equal(hash, Hash(test));

        test.Write(ClusterFile, With("updateIntervalHours: 24", "updateIntervalHours: 12"));
        Assert.NotEqual(hash, Hash(test));
        Assert.Equal(digest, Digest(test));
        test.Write(ClusterFile, Definition);

        test.Write(SecondaryFile, Guest("moraband", 420, "10.1.0.43"));
        Assert.NotEqual(hash, Hash(test));
        Assert.Equal(digest, Digest(test));
        test.Write(SecondaryFile, Guest("moraband", 420, "10.1.0.42"));

        test.Write("modules/opentofu/cluster/main.tf", "# another root\n");
        Assert.NotEqual(hash, Hash(test));
        Assert.NotEqual(digest, Digest(test));
    }

    [Fact]
    public void The_trusted_code_includes_each_members_recipe()
    {
        using var test = Repository();
        var digest = Digest(test);
        var hash = Hash(test);
        test.Write("modules/opentofu/test-vm/main.tf", "# another root\n");
        Assert.NotEqual(digest, Digest(test));
        Assert.NotEqual(hash, Hash(test));
    }

    [Fact]
    public void The_trusted_code_includes_the_image_that_runs_it()
    {
        using var test = Repository();
        var digest = Digest(test);
        test.Write("recipes/technitium-cluster/recipe.yaml", """
            schemaVersion: 1
            operation: cluster
            executor: opentofu
            root: ../../modules/opentofu/cluster
            image: registry.example.invalid/evil:latest
            """);
        Assert.NotEqual(digest, Digest(test));
    }

    private static ClusterInputs Inputs(TestRepository test)
    {
        var repository = LabRepositoryLoader.Load(test.Root);
        return ClusterInputs.Build(repository, repository.Cluster(Cluster)!, "abc");
    }

    private static string Hash(TestRepository test) => Inputs(test).Hash;

    private static string Digest(TestRepository test) => Inputs(test).CodeDigest;

    [Fact]
    public void The_schema_is_embedded_for_the_editor()
    {
        Assert.Contains("\"members\"", SchemaCatalog.SchemaText("cluster"));
    }
}
