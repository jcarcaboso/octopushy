using System.Text;
using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

/// <summary>A compose file is repository content: what it names is read as written, and anything odd shows nothing.</summary>
public class ComposeImagesTests
{
    private const string Directory = "labs/home/proxmox/korriban/guests/krb-app-01/services/fleet";

    [Fact]
    public void Each_service_with_an_image_string_is_read_as_written()
    {
        var images = ComposeImages.Read("""
            name: fleet
            x-common: &common { restart: always }
            services:
              app:
                <<: *common
                image: ghcr.io/example/fleet-manager:0.12.4
                environment: { TAG: "${TAG}" }
              db:
                image: "docker.io/library/postgres:17.2-alpine"
              built:
                build: ./app
              templated:
                image: registry.example/${IMAGE:-app}:latest
            volumes: { data: {} }
            """);

        Assert.Equal(new Dictionary<string, string>
        {
            ["app"] = "ghcr.io/example/fleet-manager:0.12.4",
            ["db"] = "docker.io/library/postgres:17.2-alpine",
            ["templated"] = "registry.example/${IMAGE:-app}:latest",
        }, images);
    }

    [Theory]
    [InlineData("compose.yaml")]
    [InlineData("compose.yml")]
    [InlineData("docker-compose.yaml")]
    [InlineData("docker-compose.yml")]
    public void Each_file_name_is_found_in_the_service_folder(string name)
    {
        Assert.Equal($"{Directory}/{name}", ComposeImages.FileIn(Directory, [$"{Directory}/service.yaml", $"{Directory}/{name}"]));
    }

    [Fact]
    public void The_first_file_name_wins_and_other_folders_do_not_count()
    {
        Assert.Equal($"{Directory}/compose.yml",
            ComposeImages.FileIn(Directory, [$"{Directory}/docker-compose.yaml", $"{Directory}/compose.yml"]));
        Assert.Null(ComposeImages.FileIn(Directory, [$"{Directory}/sub/compose.yaml", $"{Directory}-old/compose.yaml", $"{Directory}/service.yaml"]));
    }

    [Fact]
    public void A_service_without_an_image_or_with_a_non_string_image_is_left_out()
    {
        var images = ComposeImages.Read("""
            services:
              none: { build: . }
              number: { image: 5 }
              bool: { image: true }
              empty: { image: }
              list: { image: [a, b] }
              map: { image: { name: a } }
              tagged: { image: !custom a }
              quoted: { image: "5" }
              long: { image: STRING }
              control: { image: "a\tb" }
              scalar: just-text
              ok: { image: nginx:1.27 }
            """.Replace("STRING", new string('a', ComposeImages.MaxReferenceLength + 1)));

        Assert.Equal(new Dictionary<string, string> { ["quoted"] = "5", ["ok"] = "nginx:1.27" }, images);
    }

    [Fact]
    public void An_image_named_through_an_alias_is_the_scalar_it_names()
    {
        var images = ComposeImages.Read("""
            x-image: &image ghcr.io/example/app:1.0
            services:
              a: { image: *image }
              b: { image: *missing }
            """);

        Assert.Equal(new Dictionary<string, string> { ["a"] = "ghcr.io/example/app:1.0" }, images);
    }

    [Fact]
    public void An_alias_bomb_is_never_expanded()
    {
        var text = new StringBuilder("a0: &a0 [x, x, x, x, x, x, x, x, x, x]\n");
        for (var i = 1; i < 30; i++)
            text.Append($"a{i}: &a{i} [*a{i - 1}, *a{i - 1}, *a{i - 1}, *a{i - 1}, *a{i - 1}, *a{i - 1}, *a{i - 1}, *a{i - 1}, *a{i - 1}, *a{i - 1}]\n");
        text.Append("services:\n  app: { image: nginx:1.27 }\n  bomb: *a29\n");

        Assert.Equal(new Dictionary<string, string> { ["app"] = "nginx:1.27" }, ComposeImages.Read(text.ToString()));
    }

    [Theory]
    [InlineData("services: [unclosed")]
    [InlineData("services:\n  app: { image: a\n  b: : :")]
    [InlineData("just a string")]
    [InlineData("- a\n- b")]
    [InlineData("services: [a, b]")]
    [InlineData("services: *nowhere")]
    [InlineData("services:\n  app: { image: a }\n  app: { image: b }")]
    [InlineData("services:\n  app: { image: a, image: b }")]
    [InlineData("services: {}\nservices: {}")]
    [InlineData("? [a]\n: b")]
    public void Invalid_yaml_or_an_unexpected_shape_shows_nothing(string text)
    {
        Assert.Null(ComposeImages.Read(text));
    }

    [Theory]
    [InlineData("[", "]")]
    [InlineData("{a: ", "}")]
    [InlineData("[\"]\", ", "]")]
    public void Deep_nesting_shows_nothing_and_stops_early(string open, string close)
    {
        // The parser takes seconds over forty thousand nested brackets: reading has to stop long before.
        const int depth = 100_000 / 8;
        var deep = "x: " + string.Concat(Enumerable.Repeat(open, depth)) + string.Concat(Enumerable.Repeat(close, depth)) + "\nservices: { app: { image: a } }";
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(ComposeImages.Read(deep));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"{timer.Elapsed}");

        var shallow = "x: " + new string('[', 30) + new string(']', 30) + "\nservices: { app: { image: a } }";
        Assert.Equal("a", ComposeImages.Read(shallow)!["app"]);
    }

    [Fact]
    public void A_file_with_no_services_has_no_images()
    {
        Assert.Empty(ComposeImages.Read("")!);
        Assert.Empty(ComposeImages.Read("name: x\n")!);
    }

    [Fact]
    public void An_oversized_file_is_not_read()
    {
        var padding = "# " + new string('x', ComposeImages.MaxFileBytes) + "\n";
        Assert.Null(ComposeImages.Read(padding + "services: { app: { image: a } }"));
    }

    [Fact]
    public void Too_many_services_show_nothing()
    {
        string File(int count) => "services:\n" + string.Concat(Enumerable.Range(0, count).Select(i => $"  s{i}: {{ image: app:{i} }}\n"));

        Assert.Equal(ComposeImages.MaxServices, ComposeImages.Read(File(ComposeImages.MaxServices))!.Count);
        Assert.Null(ComposeImages.Read(File(ComposeImages.MaxServices + 1)));
    }
}
