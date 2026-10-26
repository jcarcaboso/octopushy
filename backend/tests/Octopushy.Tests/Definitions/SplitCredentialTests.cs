using System.Text.Json.Nodes;
using Octopushy.Definitions;
using Octopushy.Definitions.Schemas;
using Octopushy.Definitions.Yaml;

namespace Octopushy.Tests.Definitions;

public class SplitCredentialTests
{
    private const string File = "labs/home/proxmox/korriban/installation.yaml";

    [Fact]
    public void Split_mode_requires_both_references_and_rejects_mixed_modes()
    {
        using var repo = TestRepository.Minimal();
        var json = YamlDocumentReader.Read(System.IO.File.ReadAllText(Path.Combine(repo.Root, File))).Root!.AsObject();
        var connection = json["connection"]!.AsObject();
        connection.Remove("token");
        connection["readOnlyToken"] = new JsonObject { ["store"] = "proxmox/read", ["version"] = 2 };
        connection["privilegedToken"] = new JsonObject { ["store"] = "proxmox/write", ["version"] = 3 };
        Assert.Empty(SchemaCatalog.Instance.Validate(DefinitionKind.Installation, json));
        repo.Write(File, json.ToJsonString());
        var loaded = LabRepositoryLoader.Load(repo.Root);
        Assert.Empty(loaded.Issues);
        Assert.Contains(new StoreUse("proxmox/read", "home/korriban", File, 2), loaded.StoreUses);
        Assert.Contains(new StoreUse("proxmox/write", "home/korriban", File, 3), loaded.StoreUses);

        connection["token"] = new JsonObject { ["store"] = "proxmox/legacy" };
        Assert.NotEmpty(SchemaCatalog.Instance.Validate(DefinitionKind.Installation, json));
        repo.Write(File, json.ToJsonString());
        Assert.False(LabRepositoryLoader.Load(repo.Root).IsValid("home/korriban/krb-app-01"));
        connection.Remove("token");
        connection.Remove("privilegedToken");
        Assert.NotEmpty(SchemaCatalog.Instance.Validate(DefinitionKind.Installation, json));
    }

    [Theory]
    [InlineData("same-key")]
    [InlineData("reserved-plain")]
    [InlineData("reserved-secret")]
    [InlineData("writer-alias")]
    [InlineData("writer-ca")]
    [InlineData("cross-reader")]
    [InlineData("cross-ca")]
    public void Configuration_cannot_rebind_provider_environment_or_alias_a_privileged_reference(string attack)
    {
        using var repo = TestRepository.Minimal();
        var json = YamlDocumentReader.Read(System.IO.File.ReadAllText(Path.Combine(repo.Root, File))).Root!.AsObject();
        var connection = json["connection"]!.AsObject();
        if (attack.StartsWith("cross"))
        {
            var other = json.DeepClone();
            other["connection"]!["readOnlyToken"]!["store"] = "proxmox/other-reader";
            other["connection"]!["privilegedToken"]!["store"] = "proxmox/other-writer";
            repo.Write("labs/home/proxmox/other/installation.yaml", other.ToJsonString());
            if (attack == "cross-reader")
                connection["readOnlyToken"]!["store"] = "proxmox/other-writer";
            else
                connection["tls"]!["caCertificate"] = new JsonObject { ["store"] = "proxmox/other-writer" };
        }
        if (attack == "same-key")
            connection["readOnlyToken"] = connection["privilegedToken"]!.DeepClone();
        if (attack == "writer-ca")
            connection["tls"]!["caCertificate"] = connection["privilegedToken"]!.DeepClone();
        repo.Write(File, json.ToJsonString());
        if (attack.StartsWith("reserved") || attack == "writer-alias")
        {
            var settings = YamlDocumentReader.Read(System.IO.File.ReadAllText(Path.Combine(repo.Root, "octopushy.yaml"))).Root!;
            settings["stateBackends"]!["postgres"]!["env"]![attack == "writer-alias" ? "PG_EXTRA" : "PROXMOX_VE_API_TOKEN"] =
                attack == "reserved-plain" ? JsonValue.Create("injected-token") : connection["privilegedToken"]!.DeepClone();
            repo.Write("octopushy.yaml", settings.ToJsonString());
        }
        var loaded = LabRepositoryLoader.Load(repo.Root);
        Assert.False(loaded.IsValid("home/korriban/krb-app-01"));
        Assert.Contains(loaded.Issues, i => i.Severity == IssueSeverity.Error &&
            (i.Message.Contains("privileged", StringComparison.OrdinalIgnoreCase) || i.Message.Contains("reserved", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("readOnlyToken")]
    [InlineData("privilegedToken")]
    public void Each_role_and_exact_version_affects_the_guest_input_hash(string role)
    {
        using var repo = TestRepository.Minimal();
        var before = LabRepositoryLoader.Load(repo.Root);
        var oldHash = ProvisionInputs.Build(before, Assert.Single(before.Guests), "revision").Hash;
        var json = YamlDocumentReader.Read(System.IO.File.ReadAllText(Path.Combine(repo.Root, File))).Root!;
        json["connection"]![role]!["version"] = 7;
        repo.Write(File, json.ToJsonString());
        var after = LabRepositoryLoader.Load(repo.Root);
        Assert.NotEqual(oldHash, ProvisionInputs.Build(after, Assert.Single(after.Guests), "revision").Hash);
    }
}
