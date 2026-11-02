using Octopushy.Definitions;
using Octopushy.Server.Orchestration;

namespace Octopushy.Tests.Definitions;

/// <summary>What makes a recipe that takes a secret reviewable, on the shipped technitium-lxc recipe.</summary>
public class ReviewedCodeTests
{
    private const string Guest = "home/korriban/krb-dns-01";
    private const string GuestFile = "labs/home/proxmox/korriban/guests/krb-dns-01/guest.yaml";
    private const string Root = "modules/opentofu/technitium-lxc";

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

    [Fact]
    public void What_the_root_calls_is_listed_so_a_change_to_it_reaches_the_guests_digest()
    {
        var shipped = LabRepositoryLoader.Load(TestRepository.ExampleLab);
        var before = ReleaseBinding.Of(shipped, shipped.Guest(Guest)!).Digest;
        foreach (var file in new[] { "modules/opentofu/proxmox-lxc/main.tf", $"{Root}/install.sh", $"{Root}/main.tf" })
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
        var recipe = Path.Combine(test.Root, "recipes/technitium-lxc/recipe.yaml");
        var text = File.ReadAllText(recipe);
        const string listed = "modules: [../../modules/opentofu/proxmox-lxc]";
        Assert.Contains(listed, text);
        File.WriteAllText(recipe, text.Replace(listed, ""));
        Assert.Contains(Errors(test), e => e.Contains("calls a module from ../proxmox-lxc"));
    }

    [Fact]
    public void A_root_without_a_lock_file_cannot_be_reviewed()
    {
        using var test = Example();
        File.Delete(Path.Combine(test.Root, Root, ".terraform.lock.hcl"));
        Assert.Contains(Errors(test), e => e.Contains(".terraform.lock.hcl"));
    }

    [Theory]
    [InlineData("provider \"x\" { token = \"$${var.x}\" }")]
    [InlineData("locals { x = \"%%{ if true }\" }")]
    [InlineData("locals { x = \"${ upper(\"a\") /* hidden */ }\" }")]
    public void The_review_scanner_refuses_what_it_cannot_read_with_certainty(string code)
    {
        using var test = Example();
        File.WriteAllText(Path.Combine(test.Root, Root, "extra.tf"), code);
        Assert.Contains(Errors(test), e => e.Contains("extra.tf cannot be read with certainty"));
    }

    [Fact]
    public void A_form_of_code_the_scanner_does_not_read_is_refused()
    {
        using var test = Example();
        File.WriteAllText(Path.Combine(test.Root, Root, "extra.tofu"), "# x");
        Assert.Contains(Errors(test), e => e.Contains("extra.tofu is a form of OpenTofu code that Octopushy does not read"));
    }

    [Fact]
    public void A_guest_that_leaves_a_secret_out_is_invalid()
    {
        using var test = Example();
        var guest = Path.Combine(test.Root, GuestFile);
        var text = File.ReadAllText(guest);
        var changed = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^  secrets:\n\s+admin_password:.*\n", "");
        Assert.NotEqual(text, changed);
        File.WriteAllText(guest, changed);
        Assert.Contains(Errors(test), e => e.Contains("needs provision.secrets.admin_password"));
    }
}
