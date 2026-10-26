using System.Text.Json.Nodes;
using Octopushy.Definitions;
using Octopushy.Definitions.Schemas;
using Octopushy.Definitions.Yaml;

namespace Octopushy.Tests.Definitions;

public class SharedCredentialTests
{
    private const string File = "labs/home/proxmox/korriban/installation.yaml";

    [Fact]
    public void One_explicit_token_is_sufficient_and_preserves_its_exact_version()
    {
        using var repo = TestRepository.Minimal();
        var json = YamlDocumentReader.Read(System.IO.File.ReadAllText(Path.Combine(repo.Root, File))).Root!.AsObject();
        var connection = json["connection"]!.AsObject();
        connection.Remove("readOnlyToken");
        connection.Remove("privilegedToken");
        connection["token"] = new JsonObject { ["store"] = "proxmox/shared", ["version"] = 2 };
        Assert.Empty(SchemaCatalog.Instance.Validate(DefinitionKind.Installation, json));
        repo.Write(File, json.ToJsonString());

        var loaded = LabRepositoryLoader.Load(repo.Root);
        Assert.Empty(loaded.Issues);
        Assert.True(loaded.IsValid("home/korriban/krb-app-01"));
        var definition = Assert.Single(loaded.Installations).Connection;
        Assert.True(definition.SharedToken);
        Assert.Equal(new StoreReference("proxmox/shared", 2), definition.ReadOnlyToken);
        Assert.Equal(new StoreReference("proxmox/shared", 2), definition.PrivilegedToken);
        Assert.Contains(new StoreUse("proxmox/shared", "home/korriban", File, 2), loaded.StoreUses);
        Assert.DoesNotContain(loaded.StoreUses, use => use.Key.Contains("read-only") || use.Key.Contains("privileged"));
    }

    [Theory]
    [InlineData("{ store: proxmox/shared }", true)]
    [InlineData("{ store: system/internal }", false)]
    [InlineData("{ store: proxmox/shared, version: 0 }", false)]
    [InlineData("{ store: proxmox/shared, version: '2' }", false)]
    [InlineData("{ store: proxmox/shared, extra: ignored }", false)]
    public void Shared_references_use_the_existing_validation_and_system_scope_rules(string reference, bool valid)
    {
        using var repo = TestRepository.Minimal();
        var json = SharedInstallation(repo);
        json["connection"]!["token"] = YamlDocumentReader.Read(reference).Root;
        repo.Write(File, json.ToJsonString());
        var loaded = LabRepositoryLoader.Load(repo.Root);
        Assert.Equal(valid, loaded.IsValid("home/korriban/krb-app-01"));
        if (valid)
            Assert.Null(Assert.Single(loaded.Installations).Connection.ReadOnlyToken.Version);
    }

    [Theory]
    [InlineData("readOnlyToken")]
    [InlineData("privilegedToken")]
    public void Shared_mode_cannot_be_mixed_with_either_split_role(string role)
    {
        using var repo = TestRepository.Minimal();
        var json = SharedInstallation(repo);
        json["connection"]![role] = new JsonObject { ["store"] = "proxmox/other" };
        Assert.NotEmpty(SchemaCatalog.Instance.Validate(DefinitionKind.Installation, json));
        repo.Write(File, json.ToJsonString());
        Assert.False(LabRepositoryLoader.Load(repo.Root).IsValid("home/korriban/krb-app-01"));
    }

    [Theory]
    [InlineData("ca")]
    [InlineData("backend")]
    [InlineData("split-reader")]
    [InlineData("split-writer")]
    public void Shared_tokens_cannot_bypass_other_credential_roles(string alias)
    {
        using var repo = TestRepository.Minimal();
        var json = SharedInstallation(repo);
        var token = json["connection"]!["token"]!;
        if (alias == "ca")
            json["connection"]!["tls"]!["caCertificate"] = token.DeepClone();
        if (alias == "backend")
        {
            var settings = YamlDocumentReader.Read(System.IO.File.ReadAllText(Path.Combine(repo.Root, "octopushy.yaml"))).Root!;
            settings["stateBackends"]!["postgres"]!["env"]!["PG_EXTRA"] = token.DeepClone();
            repo.Write("octopushy.yaml", settings.ToJsonString());
        }
        if (alias.StartsWith("split-", StringComparison.Ordinal))
        {
            var other = YamlDocumentReader.Read(System.IO.File.ReadAllText(Path.Combine(repo.Root, File))).Root!;
            other["connection"]![alias == "split-reader" ? "readOnlyToken" : "privilegedToken"] = token.DeepClone();
            repo.Write("labs/home/proxmox/other/installation.yaml", other.ToJsonString());
        }
        repo.Write(File, json.ToJsonString());
        var loaded = LabRepositoryLoader.Load(repo.Root);
        Assert.Contains(loaded.Issues, issue => issue.Severity == IssueSeverity.Error);
        Assert.False(loaded.IsValid(alias == "split-reader" ? "home/other" : "home/korriban"));
    }

    [Fact]
    public void Explicit_shared_installations_can_reuse_a_token_without_simulating_split_roles()
    {
        using var repo = TestRepository.Minimal();
        var json = SharedInstallation(repo);
        repo.Write(File, json.ToJsonString());
        repo.Write("labs/home/proxmox/other/installation.yaml", json.ToJsonString());
        var loaded = LabRepositoryLoader.Load(repo.Root);
        Assert.Empty(loaded.Issues);
        Assert.All(loaded.Installations, installation => Assert.True(installation.Connection.SharedToken));
    }

    [Fact]
    public void Changing_token_mode_key_or_version_changes_the_provisioning_fingerprint()
    {
        using var repo = TestRepository.Minimal();
        string Hash()
        {
            var loaded = LabRepositoryLoader.Load(repo.Root);
            Assert.Empty(loaded.Issues);
            return ProvisionInputs.Build(loaded, Assert.Single(loaded.Guests), "same-revision").Hash;
        }

        var splitHash = Hash();
        var json = SharedInstallation(repo);
        repo.Write(File, json.ToJsonString());
        var sharedHash = Hash();
        Assert.NotEqual(splitHash, sharedHash);
        json["connection"]!["token"]!["version"] = 2;
        repo.Write(File, json.ToJsonString());
        var versionedHash = Hash();
        Assert.NotEqual(sharedHash, versionedHash);
        json["connection"]!["token"]!["store"] = "proxmox/replacement";
        repo.Write(File, json.ToJsonString());
        Assert.NotEqual(versionedHash, Hash());
    }

    private static JsonObject SharedInstallation(TestRepository repo)
    {
        var json = YamlDocumentReader.Read(System.IO.File.ReadAllText(Path.Combine(repo.Root, File))).Root!.AsObject();
        var connection = json["connection"]!.AsObject();
        connection.Remove("readOnlyToken");
        connection.Remove("privilegedToken");
        connection["token"] = new JsonObject { ["store"] = "proxmox/shared" };
        return json;
    }
}
