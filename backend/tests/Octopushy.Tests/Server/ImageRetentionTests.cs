using Octopushy.Server.Images;

namespace Octopushy.Tests.Server;

/// <summary>Which images the server keeps: see <see cref="ImageRetention"/> and docs/built-templates.md.</summary>
public class ImageRetentionTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static StoredImage Image(string template, char digest, long size, int day) =>
        new(Guid.NewGuid(), template, new string(digest, 64), size, Start.AddDays(day));

    private static HashSet<string> Referenced(params StoredImage[] images) => images.Select(i => i.Sha256).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void The_newest_unreferenced_images_of_each_template_are_kept_and_the_rest_go()
    {
        var current = Image("nixos", 'a', 10, 3);
        var previous = Image("nixos", 'b', 10, 2);
        var older = Image("nixos", 'c', 10, 1);
        var other = Image("debian", 'd', 10, 0);

        var plan = ImageRetention.Plan([current, previous, older, other], Referenced(current), keepUnreferenced: 1, maxBytes: 1000);

        Assert.Equal([older], plan.Delete);
        Assert.True(plan.Fits);
        Assert.Equal(30, plan.Total);
        Assert.Equal([previous, older], ImageRetention.Plan([current, previous, older], Referenced(current), 0, 1000).Delete);
    }

    [Fact]
    public void Above_the_limit_the_oldest_unreferenced_go_first_and_a_referenced_image_never_does()
    {
        var current = Image("nixos", 'a', 50, 5);
        var rollback = Image("nixos", 'b', 40, 4);
        var other = Image("debian", 'c', 30, 1);
        var needed = Image("debian", 'd', 60, 0);

        var plan = ImageRetention.Plan([current, rollback, other, needed], Referenced(current, needed), keepUnreferenced: 1, maxBytes: 120);
        Assert.Equal([other, rollback], plan.Delete);
        Assert.Equal(110, plan.Total);
        Assert.True(plan.Fits);

        // Only referenced images left, and still too much: nothing referenced goes, and it does not fit.
        var full = ImageRetention.Plan([current, needed], Referenced(current, needed), keepUnreferenced: 1, maxBytes: 100);
        Assert.Empty(full.Delete);
        Assert.False(full.Fits);
    }

    [Fact]
    public void An_upload_on_its_way_counts_against_the_limit()
    {
        var current = Image("nixos", 'a', 50, 5);
        var rollback = Image("nixos", 'b', 40, 4);

        Assert.Empty(ImageRetention.Plan([current, rollback], Referenced(current), 1, 100).Delete);
        var plan = ImageRetention.Plan([current, rollback], Referenced(current), 1, 100, incoming: 50);
        Assert.Equal([rollback], plan.Delete);
        Assert.Equal(100, plan.Total);
        Assert.True(plan.Fits);
        Assert.False(ImageRetention.Plan([current, rollback], Referenced(current), 1, 100, incoming: 51).Fits);
    }

    [Fact]
    public void Two_rows_of_the_same_image_are_one_file()
    {
        var first = Image("nixos", 'a', 50, 1);
        var same = first with { Id = Guid.NewGuid(), TemplateId = "nixos-copy", BuiltAt = Start.AddDays(2) };

        var plan = ImageRetention.Plan([first, same], Referenced(first), 1, 60);
        Assert.Empty(plan.Delete);
        Assert.Equal(50, plan.Total);
    }
}
