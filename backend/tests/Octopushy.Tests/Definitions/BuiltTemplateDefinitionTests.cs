using System.Security.Cryptography;
using System.Text;
using Octopushy.Definitions;
using Octopushy.Validate;

namespace Octopushy.Tests.Definitions;

/// <summary>A template whose image Octopushy builds from a flake output: <c>source.build</c>. See docs/built-templates.md.</summary>
public class BuiltTemplateDefinitionTests
{
    private const string Guest = "home/korriban/krb-app-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    private const string TemplateFile = "templates/nixos-base/template.yaml";
    private const string Lock = BuiltTemplateFixtures.Lock;

    private static IEnumerable<DefinitionIssue> Errors(LabRepository repository) => repository.Issues.Where(i => i.Severity == IssueSeverity.Error);

    [Fact]
    public void A_built_template_resolves_its_workflow_reads_its_lock_and_has_its_build_key()
    {
        using var test = BuiltTemplateFixtures.WithBuiltTemplate();
        var repository = LabRepositoryLoader.Load(test.Root);

        Assert.Empty(repository.Issues);
        var template = repository.Templates["nixos-base"];
        Assert.True(template.Valid);
        Assert.Null(template.Image);
        var build = Assert.IsType<BuildSource>(template.Build);
        Assert.Equal(("test-vm@1", "test-vm", "1.0", "base-image", "templates/nixos-base/flake.lock"),
            (build.WorkflowReference, build.WorkflowId, build.WorkflowVersion, build.Output, build.LockPath));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Lock + "\n"))), build.LockSha256);
        // A workflow kept in the lab repository has no release commit: its flake's files stand in for it.
        Assert.StartsWith("tree:", build.WorkflowCommit);
        Assert.Null(build.WorkflowRepository);
        Assert.Equal(KeyOf(build.WorkflowCommit, "base-image", build.LockSha256), build.Key);
        Assert.True(repository.IsValid(Guest));
    }

    [Fact]
    public void The_build_key_is_the_digest_of_the_commit_output_system_and_lock()
    {
        Assert.Equal(KeyOf("abc", "base-image", null), BuildSource.KeyOf("abc", "base-image", null));
        Assert.Equal(KeyOf("abc", "base-image", "00"), BuildSource.KeyOf("abc", "base-image", "00"));
        Assert.Matches("^[0-9a-f]{64}$", BuildSource.KeyOf("abc", "base-image", null));
        Assert.NotEqual(BuildSource.KeyOf("abc", "base-image", null), BuildSource.KeyOf("abd", "base-image", null));
        Assert.NotEqual(BuildSource.KeyOf("abc", "base-image", null), BuildSource.KeyOf("abc", "other", null));
    }

    [Fact]
    public void A_new_lock_or_a_changed_flake_is_a_new_key_and_an_unrelated_change_is_not()
    {
        using var test = BuiltTemplateFixtures.WithBuiltTemplate();
        var key = LabRepositoryLoader.Load(test.Root).Templates["nixos-base"].Build!.Key;

        test.Write("labs/home/proxmox/korriban/guests/krb-app-01/notes.txt", "unrelated");
        Assert.Equal(key, LabRepositoryLoader.Load(test.Root).Templates["nixos-base"].Build!.Key);

        test.Write("templates/nixos-base/flake.lock", "{ \"nodes\": { \"nixpkgs\": {} }, \"version\": 7 }");
        var relocked = LabRepositoryLoader.Load(test.Root).Templates["nixos-base"].Build!.Key;
        Assert.NotEqual(key, relocked);

        test.Write("modules/nixos/flake.nix", "{ outputs = { self }: { packages = { }; }; }");
        Assert.NotEqual(relocked, LabRepositoryLoader.Load(test.Root).Templates["nixos-base"].Build!.Key);
    }

    [Fact]
    public void Without_a_lock_the_key_says_none()
    {
        using var test = BuiltTemplateFixtures.WithBuiltTemplate(withLock: false);
        var build = LabRepositoryLoader.Load(test.Root).Templates["nixos-base"].Build!;

        Assert.Null(build.LockPath);
        Assert.Null(build.LockSha256);
        Assert.Equal(KeyOf(build.WorkflowCommit, "base-image", null), build.Key);
    }

    [Fact]
    public void A_missing_lock_is_an_error_and_its_guests_are_invalid()
    {
        using var test = BuiltTemplateFixtures.WithBuiltTemplate();
        test.Delete("templates/nixos-base/flake.lock");
        var repository = LabRepositoryLoader.Load(test.Root);

        var error = Assert.Single(Errors(repository));
        Assert.Equal(("templates/nixos-base", TemplateFile), (error.Address, error.File));
        Assert.Contains("source.build.lock names templates/nixos-base/flake.lock, which does not exist", error.Message);
        Assert.False(repository.Templates["nixos-base"].Valid);
        Assert.False(repository.IsValid(Guest));
        // The guest shows why: its template's issue, and not a second one about a missing image.
        Assert.Contains(repository.IssuesFor(Guest), i => i == error);
        Assert.DoesNotContain(repository.Issues, i => i.Message.Contains("no downloadable image"));
    }

    [Fact]
    public void A_workflow_that_does_not_resolve_is_an_error()
    {
        using var test = BuiltTemplateFixtures.WithBuiltTemplate();
        test.Write(TemplateFile, File.ReadAllText(Path.Combine(test.Root, TemplateFile)).Replace("workflow: test-vm@1", "workflow: test-vm@2"));
        var repository = LabRepositoryLoader.Load(test.Root);

        var error = Assert.Single(Errors(repository));
        Assert.Equal("templates/nixos-base", error.Address);
        Assert.Contains("test-vm@2 not found", error.Message);
        Assert.Null(repository.Templates["nixos-base"].Build);
        Assert.False(repository.IsValid(Guest));
    }

    [Fact]
    public void A_workflow_without_a_flake_cannot_build()
    {
        using var test = BuiltTemplateFixtures.WithBuiltTemplate();
        test.Delete("modules/nixos");
        var repository = LabRepositoryLoader.Load(test.Root);

        Assert.Contains(Errors(repository), i => i.Message.Contains("has no modules/nixos/flake.nix to build base-image from"));
        Assert.False(repository.IsValid(Guest));
    }

    [Fact]
    public void Output_is_required_and_a_lock_is_a_plain_file_name()
    {
        using var test = BuiltTemplateFixtures.WithBuiltTemplate();
        var text = File.ReadAllText(Path.Combine(test.Root, TemplateFile));
        test.Write(TemplateFile, text.Replace("    output: base-image\n", ""));
        Assert.False(LabRepositoryLoader.Load(test.Root).IsValid(Guest));

        test.Write(TemplateFile, text.Replace("lock: flake.lock", "lock: ../other/flake.lock"));
        Assert.False(LabRepositoryLoader.Load(test.Root).IsValid(Guest));
    }

    [Fact]
    public void A_built_template_keeps_its_available_on_warning()
    {
        using var test = BuiltTemplateFixtures.WithBuiltTemplate();
        test.Write(TemplateFile, File.ReadAllText(Path.Combine(test.Root, TemplateFile)).Replace("[home/korriban]", "[home/moraband]"));
        var repository = LabRepositoryLoader.Load(test.Root);

        Assert.Contains(repository.Issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("does not list home/korriban in availableOn"));
        Assert.True(repository.IsValid(Guest));
    }

    [Fact]
    public void The_offline_validator_accepts_a_built_template()
    {
        using var test = BuiltTemplateFixtures.WithBuiltTemplate();
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(Validator.Valid, Validator.Run([test.Root], output, error));
        Assert.Contains("0 errors", output.ToString());
    }

    [Fact]
    public void A_template_build_reference_is_resolved_with_the_guests_and_services()
    {
        using var test = BuiltTemplateFixtures.WithBuiltTemplate();
        test.Write(TemplateFile, File.ReadAllText(Path.Combine(test.Root, TemplateFile)).Replace("workflow: test-vm@1", "workflow: nixos-host@1"));

        Assert.Equal(["nixos-host@1", "test-vm@1"], WorkflowReferences.In(test.Root).Select(r => r.ToString()));
    }

    [Fact]
    public void A_guest_of_a_built_template_gets_the_image_in_its_variables_and_the_key_in_its_hash()
    {
        using var test = BuiltTemplateFixtures.WithBuiltTemplate();
        var repository = LabRepositoryLoader.Load(test.Root);
        var guest = repository.Guest(Guest)!;
        var image = TemplateImage.Of(new string('a', 64), 867041280, "nixos-host.qcow2");

        var inputs = ProvisionInputs.Build(repository, guest, "c", image: image);
        Assert.Same(image, inputs.Image);
        Assert.Equal("aaaaaaaaaaaa-nixos-host.qcow2", image.FileName);
        Assert.Equal(
            """{"id":"nixos-base","os":"nixos","source":{"kind":"built","url":null,"checksum":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","checksumAlgorithm":"sha256","fileName":"aaaaaaaaaaaa-nixos-host.qcow2","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":867041280}}""",
            inputs.Variables["template"]!.ToJsonString());

        // The hash is the key's, not the image's: a rebuild of the same key plans nothing, and a new key plans again.
        var withoutImage = ProvisionInputs.Build(repository, guest, "c");
        Assert.Null(withoutImage.Image);
        Assert.Equal(inputs.Hash, withoutImage.Hash);
        test.Write("templates/nixos-base/flake.lock", "{ \"nodes\": { \"nixpkgs\": {} }, \"version\": 7 }");
        var relocked = LabRepositoryLoader.Load(test.Root);
        Assert.NotEqual(inputs.Hash, ProvisionInputs.Build(relocked, relocked.Guest(Guest)!, "c", image: image).Hash);
    }

    [Fact]
    public void A_downloaded_template_keeps_its_variables_and_hash_and_ignores_an_image()
    {
        using var test = TestRepository.Minimal();
        test.Write("templates/debian-13/template.yaml", $"""
            schemaVersion: 1
            os: debian
            source:
              url: https://cloud.debian.org/images/cloud/trixie/latest/debian-13-genericcloud-amd64.qcow2
              checksum: sha256:{string.Concat(Enumerable.Repeat("ab", 32))}
            """);
        BuiltTemplateFixtures.UseTemplate(test, "debian-13");
        var repository = LabRepositoryLoader.Load(test.Root);
        var guest = repository.Guests[0];

        // What 0.7.3 (8ed3ab9) gives this repository, byte for byte.
        const string Variables = """{"octopushy":{"address":"home/korriban/krb-app-01","stack":"home.korriban.krb-app-01","lab":"home","installation":"korriban","revision":"c","workflow":"test-vm@1.0"},"guest":{"id":"krb-app-01","vmid":310,"host":"korriban","template":"debian-13","profiles":[],"hardware":{"cores":2,"memory":2048},"network":{"bridge":"vmbr0","mode":"static","address":"10.1.0.31/24","gateway":"10.1.0.1"},"sshAuthorizedKeys":[]},"inputs":{},"host":{"id":"korriban","datastores":{"images":"local","disks":"local-lvm"},"bridges":["vmbr0"]},"installation":{"id":"korriban","type":"standalone","endpoint":"https://10.1.0.10:8006/","verifyTls":false},"template":{"id":"debian-13","os":"debian","source":{"url":"https://cloud.debian.org/images/cloud/trixie/latest/debian-13-genericcloud-amd64.qcow2","checksum":"abababababababababababababababababababababababababababababababab","checksumAlgorithm":"sha256","fileName":"debian-13-genericcloud-amd64.qcow2"}}}""";
        const string Hash = "7275035a068907f0b27183856bd8d98458a97a97429985feec80f8613492a42d";
        var inputs = ProvisionInputs.Build(repository, guest, "c");
        Assert.Equal(Variables, inputs.Variables.ToJsonString());
        Assert.Equal(Hash, inputs.Hash);

        var given = ProvisionInputs.Build(repository, guest, "c", image: TemplateImage.Of(new string('a', 64), 1, "x.qcow2"));
        Assert.Null(given.Image);
        Assert.Equal(Variables, given.Variables.ToJsonString());
        Assert.Equal(Hash, given.Hash);
    }

    private static string KeyOf(string commit, string output, string? lockSha256) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"octopushy-image-build-v1\n{commit}\n{output}\nx86_64-linux\n{lockSha256 ?? "none"}")));
}

/// <summary>Repositories with a built template, for the definition and server tests alike.</summary>
public static class BuiltTemplateFixtures
{
    public const string GuestFile = "labs/home/proxmox/korriban/guests/krb-app-01/guest.yaml";
    public const string TemplateFile = "templates/nixos-base/template.yaml";
    public const string Lock = "{ \"nodes\": {}, \"version\": 7 }";

    /// <summary><see cref="TestRepository.Minimal"/> with a built template the guest uses, its lock, and a flake in the workflow's modules.</summary>
    public static TestRepository WithBuiltTemplate(bool withLock = true)
    {
        var test = TestRepository.Minimal();
        test.Write(TemplateFile, $$"""
            schemaVersion: 1
            os: nixos
            source:
              build:
                workflow: test-vm@1
                output: base-image
            {{(withLock ? "    lock: flake.lock" : "")}}
            availableOn: [home/korriban]
            """);
        if (withLock)
            test.Write("templates/nixos-base/flake.lock", Lock);
        test.Write("modules/nixos/flake.nix", "{ outputs = { self }: { }; }");
        UseTemplate(test, "nixos-base");
        return test;
    }

    public static void UseTemplate(TestRepository test, string template)
    {
        var file = Path.Combine(test.Root, GuestFile);
        File.WriteAllText(file, File.ReadAllText(file).Replace("workflow: test-vm@1", $"workflow: test-vm@1\ntemplate: {template}"));
    }
}
