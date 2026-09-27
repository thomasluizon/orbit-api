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
        var workingDirectory = Directory.GetCurrentDirectory();
        var configurationDirectory = File.Exists(Path.Combine(workingDirectory, "appsettings.json"))
            ? workingDirectory
            : Path.Combine(workingDirectory, "..", "Orbit.Api");
        var configuration = new ConfigurationBuilder()
            .SetBasePath(configurationDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        var optionsBuilder = new DbContextOptionsBuilder<OrbitDbContext>();
        optionsBuilder.UseNpgsql(OrbitConnectionStringFactory.ForSession(configuration));

        return new OrbitDbContext(optionsBuilder.Options);
    }
}
