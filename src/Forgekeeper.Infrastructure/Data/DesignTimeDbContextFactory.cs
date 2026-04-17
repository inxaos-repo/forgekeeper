using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Forgekeeper.Infrastructure.Data;

/// <summary>
/// Design-time factory for EF Core migrations tooling.
/// Used by 'dotnet ef migrations add/list/remove' commands.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ForgeDbContext>
{
    public ForgeDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ForgeDbContext>();
        
        // Use a dummy connection string for design-time migration generation
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__ForgeDb")
            ?? "Host=localhost;Database=forgekeeper;Username=forgekeeper;Password=forgekeeper";
        
        optionsBuilder.UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();

        return new ForgeDbContext(optionsBuilder.Options);
    }
}
