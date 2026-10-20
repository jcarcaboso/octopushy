using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Octopushy.Server.Data;

/// <summary>Lets <c>dotnet ef</c> build the model without starting the server.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<OctopushyDb>
{
    public OctopushyDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<OctopushyDb>()
        .UseNpgsql("Host=localhost;Database=octopushy_design")
        .UseSnakeCaseNamingConvention()
        .Options);
}
