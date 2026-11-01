using Octopushy.Definitions;

namespace Octopushy.Tests.Definitions;

/// <summary>
/// The scanner decides which code runs with a password, so what it cannot classify is refused,
/// and a file OpenTofu reads differently must not slip past it.
/// </summary>
public class TofuSourceTests
{
    /// <summary>
    /// Real OpenTofu 1.13.1 reads <c>$${</c> as the literal text <c>${</c>, ends the string at the
    /// next quote, and sees the module. A scanner that took it for an interpolation read the
    /// module as part of a string and reported only the variables.
    /// </summary>
    private const string EscapedInterpolation = """
        variable "x" {
          description = "$${" # "
          # {
        }
        module "evil" { source = "https://evil.example/m.zip" }
        variable "y" {
          # } } "
        }
        """;

    private static bool SeesTheModule(string text) =>
        TofuSource.Blocks(text) is not { } blocks || blocks.Any(b => b.Type == "module");

    [Fact]
    public void An_escaped_interpolation_does_not_hide_the_module_after_it()
    {
        Assert.True(SeesTheModule(EscapedInterpolation));
    }

    [Theory]
    [InlineData("locals { a = \"$${\" }\nmodule \"m\" { source = \"https://x.example/m.zip\" }\n")]
    [InlineData("locals { a = \"%%{\" }\nmodule \"m\" { source = \"https://x.example/m.zip\" }\n")]
    [InlineData("locals { a = \"$$${\" }\nmodule \"m\" { source = \"https://x.example/m.zip\" }\n")]
    [InlineData("locals { a = \"x$${y\" # \"\n}\nmodule \"m\" { source = \"https://x.example/m.zip\" }\n")]
    public void What_is_written_as_an_escape_is_refused_rather_than_guessed(string text)
    {
        Assert.Null(TofuSource.Blocks(text));
    }

    [Theory]
    [InlineData("locals { a = \"${ /* } */ 1 }\" }\n")]
    [InlineData("locals { a = \"${ 1 # } \n }\" }\n")]
    [InlineData("locals { a = \"${ 1 // } \n }\" }\n")]
    [InlineData("locals { a = \"${ <<EOT\n}\nEOT\n }\" }\n")]
    [InlineData("locals { a = <<EOT\n${ \"\nEOT\n\" }\nEOT\n}\nmodule \"m\" { source = \"https://x.example/m.zip\" }\n")]
    [InlineData("locals { a = <<EOT\n%{ if true }\nEOT\n}\n")]
    public void An_expression_it_cannot_follow_makes_the_file_unreadable(string text)
    {
        Assert.Null(TofuSource.Blocks(text));
    }

    [Fact]
    public void Ordinary_files_are_still_read()
    {
        var blocks = TofuSource.Blocks("""
            # module "not" { source = "../a" }
            locals {
              text = "module \"not\" { source = \"../d\" } ${upper("x}")} %{ if true }a%{ endif }"
              doc  = <<-EOT
                module "not" { source = "../e" }
              EOT
            }
            module "m" { source = "./lib" }
            """);
        var module = Assert.Single(blocks!, b => b.Type == "module");
        Assert.Equal("./lib", module.Source);
    }

    [Fact]
    public void The_shipped_roots_can_be_read()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(TestRepository.ExampleLab, "modules", "opentofu"), "*.tf", SearchOption.AllDirectories))
            Assert.True(TofuSource.Blocks(File.ReadAllText(file)) is not null, file);
    }
}
