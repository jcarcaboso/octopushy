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
        Assert.Equal(Validator.UsageError, Validator.Run([Path.Combine(test.Root, "missing")], output, error));
        Assert.Equal(Validator.Valid, Validator.Run(["--help"], output, error));
        Assert.Contains("Usage: octopushy-validate <repository>", output.ToString());
    }
}
