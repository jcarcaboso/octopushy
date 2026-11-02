using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

public class PipelineDefinitionTests
{
    internal const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    internal const string Guest = "home/korriban/krb-app-01";
    internal const string Address = Guest + "/web";
    internal const string PipelineFile = "labs/home/proxmox/korriban/guests/krb-app-01/services/web/pipeline.yaml";

    internal const string Pipeline = """
        schemaVersion: 1
        stages:
          - { name: expose, recipe: web-rules }
          - name: deploy
            recipe: web
            inputs: { port: 9000 }
            secrets:
              web_password: { store: web/password }
        inputs: { port: 8080, hostname: web-01 }
        """;

    /// <summary>The guest of <see cref="GuestConfigurationTests.Repository"/> with one service of two stages.</summary>
    internal static TestRepository Repository(string pipeline = Pipeline, string guestPolicy = "", string webRecipe = "")
    {
        var test = GuestConfigurationTests.Repository(guestPolicy);
        test.Write(PipelineFile, pipeline);
        test.Write("recipes/web-rules/recipe.yaml", """
            schemaVersion: 1
            operation: stage
            executor: opentofu
            root: ../../modules/opentofu/web-rules
            """);
        test.Write("modules/opentofu/web-rules/main.tf", """
            variable "port" {
              type = number
            }

            resource "terraform_data" "rule" {
              input = var.port
            }
            """);
        test.Write("recipes/web/recipe.yaml", $$"""
            schemaVersion: 1
            operation: configure
            executor: ansible
            root: ../../modules/ansible/web
            inputs: inputs.schema.json
            secrets: [web_password]
            actions:
              deploy:
                playbook: deploy.yml
                destructive: false
                onChange: true
            {{webRecipe}}
            """);
        test.Write("recipes/web/inputs.schema.json", """
            { "type": "object", "properties": { "port": { "type": "integer" } }, "required": ["port"] }
            """);
        test.Write("modules/ansible/web/deploy.yml", "- hosts: all\n");
        return test;
    }

    private static string Errors(TestRepository test) =>
        string.Join("\n", LabRepositoryLoader.Load(test.Root).Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message));

    private static void Replace(TestRepository test, string file, string from, string to)
    {
        var text = File.ReadAllText(Path.Combine(test.Root, file));
        Assert.Contains(from, text);
        test.Write(file, text.Replace(from, to));
    }

    [Fact]
    public void A_service_is_read_from_its_guests_folder_with_each_stages_inputs()
    {
        using var test = Repository();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Empty(repository.Issues);
        Assert.True(repository.IsValid(Address));
        var service = repository.Service(Address)!;
        Assert.Equal(Guest, service.GuestAddress);
        Assert.Equal(new[] { "expose", "deploy" }, service.Stages.Select(s => s.Name));
        Assert.Equal(8080, service.Stages[0].Inputs["port"]!.GetValue<int>());
        Assert.Equal(9000, service.Stages[1].Inputs["port"]!.GetValue<int>());
        Assert.Equal("web-01", service.Stages[1].Inputs["hostname"]!.GetValue<string>());
        Assert.Equal("web/password", service.Stages[1].Secrets["web_password"].Key);
        Assert.Contains(repository.StoreUses, u => u.Key == "web/password" && u.Consumer == Address);
    }

    [Theory]
    // Fields of later steps are refused, not ignored.
    [InlineData("""
        schemaVersion: 1
        stages:
          - name: dns
            interface: dns
            inputs:
              records: []
        """)]
    [InlineData("""
        schemaVersion: 1
        stages:
          - { name: run, recipe: web, container: web }
        """)]
    [InlineData("""
        schemaVersion: 1
        stages:
          - { name: deploy, recipe: web }
        ports: [8080]
        """)]
    [InlineData("""
        schemaVersion: 1
        stages: []
        """)]
    public void A_pipeline_outside_the_schema_is_refused(string pipeline)
    {
        using var test = Repository(pipeline);
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid(Address));
        Assert.Contains(repository.Issues, i => i.Address == Address && i.Severity == IssueSeverity.Error && i.File == PipelineFile);
        Assert.True(repository.IsValid(Guest));
    }

    [Fact]
    public void A_labs_interfaces_are_refused_until_they_do_something()
    {
        using var test = Repository();
        Replace(test, "labs/home/lab.yaml", "name: Home", "name: Home\ninterfaces:\n  web: { recipe: web }");
        Assert.False(LabRepositoryLoader.Load(test.Root).IsValid("home"));
    }

    [Fact]
    public void A_clusters_folder_is_not_read_and_says_so()
    {
        using var test = Repository();
        test.Write("labs/home/clusters/web/cluster.yaml", "members: []");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.True(repository.IsValid(Address));
        Assert.Contains(repository.Issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("Clusters are not supported yet"));
    }

    [Theory]
    [InlineData("- { name: deploy, recipe: nothing }", "does not exist under recipes/")]
    [InlineData("- { name: deploy, recipe: test-vm }", "cannot be a stage")]
    [InlineData("- { name: a, recipe: web-rules, secrets: { token: { store: web/token } } }", "gets no secrets yet")]
    [InlineData("- { name: a, recipe: web-rules, secretFiles: { token: { store: web/token } } }", "gets no secrets yet")]
    [InlineData("- { name: a, recipe: web, inputs: { port: 1 } }", "needs secrets.web_password")]
    [InlineData("- { name: a, recipe: web, inputs: { port: nope }, secrets: { web_password: { store: web/password } } }", "port")]
    [InlineData("- { name: a, recipe: web, inputs: { port: 1, web_password: x }, secrets: { web_password: { store: web/password } } }", "set more than once")]
    [InlineData("- { name: a, recipe: web, inputs: { port: 1 }, secrets: { web_password: { store: proxmox/korriban/privileged-token } } }", "Proxmox API token")]
    public void A_stage_is_checked_and_only_its_service_fails(string stage, string expected)
    {
        using var test = Repository($"schemaVersion: 1\nstages:\n  {stage}\n");
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/services/other/pipeline.yaml", "schemaVersion: 1\nstages:\n  - { name: expose, recipe: web-rules }\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Contains(expected, Errors(test));
        Assert.False(repository.IsValid(Address));
        Assert.True(repository.IsValid(Guest));
        Assert.True(repository.IsValid(Guest + "/other"));
    }

    [Fact]
    public void Stage_names_are_unique()
    {
        using var test = Repository("schemaVersion: 1\nstages:\n  - { name: a, recipe: web-rules }\n  - { name: a, recipe: web-rules }\n");
        Assert.Contains("used more than once", Errors(test));
    }

    [Fact]
    public void A_stage_needs_the_recipes_onChange_action()
    {
        using var test = Repository();
        Replace(test, "recipes/web/recipe.yaml", "    onChange: true\n", "");
        Assert.Contains("marks no action with onChange", Errors(test));
    }

    [Fact]
    public void A_pipeline_has_one_approval_so_a_recipe_that_asks_for_another_is_refused()
    {
        using var test = Repository(webRecipe: "outcomes:\n  busy:\n    approve:\n      vars: { force: true }");
        Assert.Contains("has an outcome a person approves", Errors(test));
    }

    [Theory]
    [InlineData("  user: ops\n", "needs access.user")]
    [InlineData("  hostKey: { store: access/app/host-key }\n", "needs access.hostKey")]
    public void An_ansible_stage_runs_on_the_guests_connection(string line, string expected)
    {
        using var test = Repository();
        Replace(test, GuestFile, line, "");
        Assert.Contains(expected, Errors(test));
    }

    [Fact]
    public void A_guest_without_a_setup_recipe_has_to_name_the_key_a_stage_logs_in_with()
    {
        using var test = Repository();
        var text = File.ReadAllText(Path.Combine(test.Root, GuestFile));
        test.Write(GuestFile, text[..text.IndexOf("setup:", StringComparison.Ordinal)]
            + "access:\n  user: ops\n  hostKey: { store: access/app/host-key }\n");
        Assert.Contains("needs access.sshKey, or a setup recipe", Errors(test));
    }

    [Fact]
    public void A_managed_worker_has_no_services()
    {
        using var test = Repository();
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.NotNull(repository.Service(Address));
        Replace(test, GuestFile, "setup:", "worker:\n  templateVersion: 1\n  generation: 3f2f6f0a-3d2c-4b6e-9c8f-0a1b2c3d4e5f\n  labels: [home]\n  maxConcurrency: 1\nsetup:");
        Assert.Contains("cannot have services", Errors(test));
    }

    [Fact]
    public void An_error_in_guest_yaml_does_not_read_as_its_services_having_gone()
    {
        using var test = Repository();
        Replace(test, GuestFile, "vmid: 310", "vmid: nope");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.NotNull(repository.Resource(Address));
        Assert.False(repository.IsValid(Address));
    }

    [Fact]
    public void A_service_on_an_invalid_recipe_is_invalid_and_its_issues_show_on_it()
    {
        using var test = Repository();
        Replace(test, "recipes/web-rules/recipe.yaml", "root: ../../modules/opentofu/web-rules", "root: ../../modules/opentofu/gone");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.False(repository.IsValid(Address));
        Assert.Contains(repository.IssuesFor(Address), i => i.Address == "recipes/web-rules");
    }

    private static string Hash(TestRepository test)
    {
        var repository = LabRepositoryLoader.Load(test.Root);
        return PipelineInputs.Build(repository, repository.Service(Address)!).Hash;
    }

    [Theory]
    [InlineData(PipelineFile, "port: 9000", "port: 9001")]
    [InlineData("recipes/web/recipe.yaml", "destructive: false", "destructive: true")]
    [InlineData("modules/ansible/web/deploy.yml", "- hosts: all", "- hosts: web")]
    [InlineData("modules/opentofu/web-rules/main.tf", "var.port", "var.port + 1")]
    [InlineData(GuestFile, "user: ops", "user: deploy")]
    public void The_hash_follows_what_a_run_is_built_from(string file, string from, string to)
    {
        using var test = Repository();
        var before = Hash(test);
        Replace(test, file, from, to);
        Assert.NotEqual(before, Hash(test));
    }

    [Fact]
    public void A_file_in_the_services_folder_changes_its_hash()
    {
        using var test = Repository();
        var before = Hash(test);
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/services/web/app.conf", "listen 80");
        Assert.NotEqual(before, Hash(test));
    }

    [Fact]
    public void Policy_labels_and_a_description_are_not_inputs()
    {
        using var test = Repository();
        var before = Hash(test);
        Replace(test, PipelineFile, "stages:", "description: A web server\npolicy: { approval: manual }\nexecution: { agentLabels: [web] }\nstages:");
        Assert.Equal(before, Hash(test));
    }

    [Fact]
    public void A_guests_setup_and_a_sibling_are_not_inputs()
    {
        using var test = Repository();
        var before = Hash(test);
        Replace(test, GuestFile, "greeting: hello", "greeting: hi");
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/services/other/pipeline.yaml", "schemaVersion: 1\nstages:\n  - { name: expose, recipe: web-rules }\n");
        Assert.Equal(before, Hash(test));
    }

    [Fact]
    public void A_service_does_not_configure_or_provision_its_guest_again()
    {
        using var test = Repository();
        var first = LabRepositoryLoader.Load(test.Root);
        var configure = ConfigureInputs.Build(first, first.Guest(Guest)!).Hash;
        var provision = ProvisionInputs.Build(first, first.Guest(Guest)!, "c").Hash;
        Replace(test, PipelineFile, "port: 9000", "port: 9001");
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/services/web/app.conf", "listen 80");
        var second = LabRepositoryLoader.Load(test.Root);
        Assert.Equal(configure, ConfigureInputs.Build(second, second.Guest(Guest)!).Hash);
        Assert.Equal(provision, ProvisionInputs.Build(second, second.Guest(Guest)!, "c").Hash);
    }

    [Fact]
    public void With_no_policy_a_service_takes_what_is_configured_and_adds_only_its_override()
    {
        using var test = Repository();
        Replace(test, "octopushy.yaml", "schemaVersion: 1", "schemaVersion: 1\ndefaults:\n  policy: { approval: manual }");
        var repository = LabRepositoryLoader.Load(test.Root);
        var policy = PolicyResolver.For(repository, repository.Guest(Guest)!, repository.Service(Address)!);
        Assert.Equal("manual", policy.Approval.Value);
        Assert.Equal("octopushy.yaml", policy.Approval.SetBy);

        Replace(test, PipelineFile, "stages:", "policy: { approval: automatic, destructive: manual }\nstages:");
        repository = LabRepositoryLoader.Load(test.Root);
        policy = PolicyResolver.For(repository, repository.Guest(Guest)!, repository.Service(Address)!);
        Assert.Equal("automatic", policy.Approval.Value);
        Assert.Equal(Address, policy.Approval.SetBy);
        Assert.Equal("manual", policy.Destructive.Value);
    }

    [Fact]
    public void Nothing_set_anywhere_means_a_service_deploys_automatically()
    {
        using var test = Repository();
        var repository = LabRepositoryLoader.Load(test.Root);
        var policy = PolicyResolver.For(repository, repository.Guest(Guest)!, repository.Service(Address)!);
        Assert.False(policy.ApprovalRequired);
        Assert.False(policy.DestructiveApprovalRequired);
        Assert.Equal("default", policy.Approval.SetBy);
    }

    [Fact]
    public void A_service_narrows_the_labels_its_guest_resolves_to()
    {
        using var test = Repository();
        Replace(test, PipelineFile, "stages:", "execution: { agentLabels: [web] }\nstages:");
        var repository = LabRepositoryLoader.Load(test.Root);
        Assert.Equal(new[] { "web" }, PolicyResolver.AgentLabels(repository, repository.Guest(Guest)!, repository.Service(Address)!).Value);
    }

    [Fact]
    public void Provision_and_configure_recipes_keep_their_shape_and_a_stage_recipe_takes_no_guest_settings()
    {
        using var test = Repository();
        Replace(test, "recipes/web-rules/recipe.yaml", "executor: opentofu", "executor: opentofu\nverify: none");
        Assert.False(LabRepositoryLoader.Load(test.Root).IsValid(Address));
        Assert.Contains(LabRepositoryLoader.Load(test.Root).Issues, i => i.Address == "recipes/web-rules" && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void A_stage_recipe_does_not_provision_a_guest()
    {
        using var test = Repository();
        Replace(test, GuestFile, "recipe: test-vm", "recipe: web-rules");
        Assert.Contains("does not provision guests", Errors(test));
    }
}
