using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

public class PolicyAndInputsTests
{
    [Fact]
    public void Defaults_are_automatic_and_destructive_follows_approval()
    {
        using var test = TestRepository.Minimal();
        var repository = LabRepositoryLoader.Load(test.Root);
        var policy = PolicyResolver.For(repository, repository.Guests[0]);
        Assert.Equal("automatic", policy.Approval.Value);
        Assert.Equal("default", policy.Approval.SetBy);
        Assert.Equal("automatic", policy.Destructive.Value);
        Assert.Equal("default, following approval", policy.Destructive.SetBy);
    }

    [Fact]
    public void The_nearest_setting_wins_and_destructive_follows_a_manual_approval()
    {
        using var test = TestRepository.Minimal();
        test.Write("octopushy.yaml", "schemaVersion: 1\ndefaults:\n  policy: { approval: automatic }\nstateBackends:\n  postgres: { type: pg }\n");
        test.Write("labs/home/lab.yaml", "schemaVersion: 1\npolicy: { approval: manual }\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        var policy = PolicyResolver.For(repository, repository.Guests[0]);
        Assert.Equal(("manual", "home"), (policy.Approval.Value, policy.Approval.SetBy));
        Assert.Equal("manual", policy.Destructive.Value);
    }

    [Fact]
    public void A_destructive_setting_flows_down_on_its_own()
    {
        using var test = TestRepository.Minimal();
        test.Write("labs/home/lab.yaml", "schemaVersion: 1\npolicy: { destructive: manual }\n");
        var repository = LabRepositoryLoader.Load(test.Root);
        var policy = PolicyResolver.For(repository, repository.Guests[0]);
        Assert.Equal("automatic", policy.Approval.Value);
        Assert.Equal(("manual", "home"), (policy.Destructive.Value, policy.Destructive.SetBy));
    }

    [Fact]
    public void Agent_labels_come_from_the_nearest_level()
    {
        using var test = TestRepository.Minimal();
        var repository = LabRepositoryLoader.Load(test.Root);
        var labels = PolicyResolver.AgentLabels(repository, repository.Guests[0]);
        Assert.Equal(["home"], labels.Value);
        Assert.Equal("home", labels.SetBy);
    }

    [Fact]
    public void The_inputs_hash_ignores_policy_and_follows_the_recipe_files()
    {
        using var test = TestRepository.Minimal();
        var before = Hash(test);
        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml", File.ReadAllText(Path.Combine(test.Root, "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml")) + "policy: { approval: manual }\ndescription: changed\n");
        Assert.Equal(before, Hash(test));
        test.Write("modules/opentofu/test-vm/extra.tf", "# a comment\n");
        Assert.NotEqual(before, Hash(test));
    }

    [Fact]
    public void Variables_follow_the_contract()
    {
        using var test = TestRepository.Minimal();
        var repository = LabRepositoryLoader.Load(test.Root);
        var inputs = ProvisionInputs.Build(repository, repository.Guests[0], "abc");
        Assert.Equal("home/korriban/krb-app-01", inputs.Variables["octopushy"]!["address"]!.GetValue<string>());
        Assert.Equal("abc", inputs.Variables["octopushy"]!["revision"]!.GetValue<string>());
        Assert.Equal(310, inputs.Variables["guest"]!["vmid"]!.GetValue<int>());
        Assert.Equal("local", inputs.Variables["host"]!["datastores"]!["images"]!.GetValue<string>());
        Assert.False(inputs.Variables["installation"]!["verifyTls"]!.GetValue<bool>());
        Assert.Null(inputs.Variables["template"]);
        Assert.Equal("10.1.0.31", ProvisionInputs.ExpectedIpv4(repository.Guests[0]));
        // The revision is not part of the hash, so a commit that changes nothing proposes nothing.
        Assert.Equal(inputs.Hash, ProvisionInputs.Build(repository, repository.Guests[0], "def").Hash);
    }

    private static string Hash(TestRepository test)
    {
        var repository = LabRepositoryLoader.Load(test.Root);
        return ProvisionInputs.Build(repository, repository.Guests[0], "rev").Hash;
    }
}
