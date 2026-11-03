using Octopushy.Validate;

namespace Octopushy.Tests.Definitions;

/// <summary>The offline validator: what it prints, and how it exits.</summary>
public class ValidatorTests
{
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string Guest = "home/korriban/krb-app-01";

    [Fact]
    public void The_example_lab_is_valid_and_each_reference_is_shown_with_what_it_resolved_to()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(Validator.Valid, Validator.Run([TestRepository.ExampleLab], output, error));
        Assert.Contains("home/korriban/krb-app-01: machine-tools@1 -> machine-tools@1.0", output.ToString());
        Assert.Contains("Workflows: machine-tools@1.0, proxmox-vm@1.0", output.ToString());
        Assert.Contains("0 errors", output.ToString());
    }

    [Fact]
    public void An_error_fails_it_and_a_warning_is_printed_without_failing_it()
    {
        using var test = TestRepository.Minimal();
        test.Write("labs/home/proxmox/korriban/guests/empty/.keep", "");
        using var warned = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(Validator.Valid, Validator.Run([test.Root], warned, error));
        Assert.Contains("warning: The guest folder", warned.ToString());

        test.Write(GuestFile, File.ReadAllText(Path.Combine(test.Root, GuestFile)).Replace("workflow: test-vm@1", "workflow: test-vm@3"));
        using var invalid = new StringWriter();
        Assert.Equal(Validator.Invalid, Validator.Run([test.Root], invalid, error));
        Assert.Contains($"{GuestFile}:7: error: test-vm@3 not found; available 1.0. [{Guest}]", invalid.ToString());
        Assert.Contains($"{Guest}: test-vm@3 -> nothing (invalid)", invalid.ToString());
    }

    [Fact]
    public void Arguments_it_cannot_use_are_a_usage_error()
    {
        using var test = TestRepository.Minimal();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(Validator.UsageError, Validator.Run([], output, error));
        Assert.Equal(Validator.UsageError, Validator.Run([test.Root, test.Root], output, error));
        Assert.Equal(Validator.UsageError, Validator.Run(["--workflows", "x", test.Root], output, error));
        Assert.Equal(Validator.UsageError, Validator.Run([test.Root, "--workflows"], output, error));
        Assert.Equal(Validator.UsageError, Validator.Run(["--workflows-only", test.Root, test.Root], output, error));
        Assert.Equal(Validator.UsageError, Validator.Run([Path.Combine(test.Root, "missing")], output, error));
        Assert.Equal(Validator.Valid, Validator.Run(["--help"], output, error));
        Assert.Contains("Usage: octopushy-validate <config-dir> [--workflows <git url|path>]", output.ToString());
    }

    [Fact]
    public void A_workflows_repository_is_resolved_by_its_tags_as_the_server_does()
    {
        using var workflows = TestWorkflows.Create();
        var first = workflows.Head;
        workflows.WriteVersion("1.1");
        var second = workflows.Commit("test-vm 1.1");
        workflows.Tag("test-vm/v1.1");
        workflows.WriteVersion("1.1", "Tagged with the wrong version.");
        workflows.Commit("1.2 without its bump");
        workflows.Tag("test-vm/v1.2");
        workflows.WriteVersion("1.3", "Untagged.");
        workflows.Commit("untagged");
        using var config = workflows.Config();
        config.Write("labs/home/proxmox/korriban/guests/krb-app-02/guest.yaml",
            File.ReadAllText(Path.Combine(config.Root, GuestFile)).Replace("vmid: 310", "vmid: 311").Replace("workflow: test-vm@1", "workflow: test-vm@1.0"));

        // As octopushy.yaml says, and with the same repository named as a URL.
        foreach (var args in new[] { new[] { config.Root }, [config.Root, "--workflows", $"file://{workflows.Root}"] })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(Validator.Valid, Validator.Run(args, output, error));
            var text = output.ToString();
            Assert.Contains($"{Guest}: test-vm@1 -> test-vm@1.1 at {second[..10]}", text);
            Assert.Contains($"home/korriban/krb-app-02: test-vm@1.0 -> test-vm@1.0 at {first[..10]}", text);
            Assert.Contains("warning: The release test-vm/v1.2 at", text);
            Assert.Contains("declares version \"1.1\", and the tag says 1.2.", text);
            Assert.Contains("0 errors, 1 warning.", text);
        }

        // A reference to no release fails it.
        config.Write(GuestFile, File.ReadAllText(Path.Combine(config.Root, GuestFile)).Replace("workflow: test-vm@1", "workflow: test-vm@2"));
        using var invalid = new StringWriter();
        using var invalidError = new StringWriter();
        Assert.Equal(Validator.Invalid, Validator.Run([config.Root], invalid, invalidError));
        Assert.Contains($"error: test-vm@2 not found in {workflows.Root}; available 1.0, 1.1.", invalid.ToString());
    }

    [Fact]
    public void A_directory_of_workflows_is_read_as_it_is_with_no_tags()
    {
        using var workflows = TestWorkflows.Create(tag: false);
        workflows.WriteVersion("1.4");
        using var config = workflows.Config();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(Validator.Valid, Validator.Run([config.Root, "--workflows", workflows.Root], output, error));
        Assert.Contains($"{Guest}: test-vm@1 -> test-vm@1.4", output.ToString());
        Assert.Contains($"Workflows from {workflows.Root}", output.ToString());

        // A lab repository may not hold its own beside them.
        using var mixed = TestRepository.Minimal();
        using var refused = new StringWriter();
        Assert.Equal(Validator.Invalid, Validator.Run([mixed.Root, "--workflows", workflows.Root], refused, error));
        Assert.Contains($"workflows: error: Workflows come from {workflows.Root}; remove workflows/ and modules/ here.", refused.ToString());
    }

    [Fact]
    public void Workflows_only_checks_every_workflow_and_its_paths_with_no_lab_repository()
    {
        using var workflows = TestWorkflows.Create(tag: false);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(Validator.Valid, Validator.Run(["--workflows-only", workflows.Root], output, error));
        Assert.Contains("Workflows: test-vm@1.0", output.ToString());
        Assert.Contains("1 workflow: 0 errors, 0 warnings.", output.ToString());

        workflows.Files.Write("workflows/broken/workflow.yaml", """
            schemaVersion: 1
            version: "1.0"
            manages: service
            stages:
              - { name: deploy, executor: ansible, root: ../../modules/ansible/missing, playbook: deploy.yml }
            """);
        using var invalid = new StringWriter();
        Assert.Equal(Validator.Invalid, Validator.Run(["--workflows-only", workflows.Root], invalid, error));
        Assert.Contains("The root modules/ansible/missing of stage 'deploy' does not exist.", invalid.ToString());
        Assert.Contains("broken@1.0 (invalid)", invalid.ToString());
    }
}
