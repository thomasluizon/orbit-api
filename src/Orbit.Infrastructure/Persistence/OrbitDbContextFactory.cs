using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Orbit.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for OrbitDbContext. Used by EF Core tools (dotnet ef migrations add, etc.)
/// when the full application service provider is not available.
/// Creates the DbContext without IEncryptionService (encryption converters disabled at design time).
/// </summary>
public class OrbitDbContextFactory : IDesignTimeDbContextFactory<OrbitDbContext>
{
    public OrbitDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.Combine(Directory.GetCurrentDirectory(), "..", "Orbit.Api"))
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var optionsBuilder = new DbContextOptionsBuilder<OrbitDbContext>();
        optionsBuilder.UseNpgsql(configuration.GetConnectionString("DefaultConnection"));

        // No encryption service at design time -- converters won't be applied
        return new OrbitDbContext(optionsBuilder.Options);
    }
}
