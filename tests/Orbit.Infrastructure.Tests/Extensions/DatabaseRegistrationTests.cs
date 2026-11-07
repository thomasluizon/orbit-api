using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Api.Extensions;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Extensions;

public sealed class DatabaseRegistrationTests
{
    [Fact]
    public void RequestScopes_ReuseTheConnectionStringWithoutBindingConfigurationAgain()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        var source = new CountingConfigurationSource(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=orbit;Username=orbit;Password=orbit",
            ["Database:EfMaxPoolSize"] = "7"
        });
        ((IConfigurationBuilder)builder.Configuration).Add(source);
        builder.Services.AddHttpContextAccessor();
        builder.AddOrbitDatabase();
        using var provider = builder.Services.BuildServiceProvider();
        source.Reads = 0;

        for (var request = 0; request < 10; request++)
        {
            using var scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<OrbitDbContext>().Database.GetConnectionString()
                .Should().Contain("Maximum Pool Size=7");
        }

        source.Reads.Should().Be(0, "a request scope must not bind DatabaseConnectionSettings through reflection again");
    }

    private sealed class CountingConfigurationSource(IDictionary<string, string?> values) : IConfigurationSource
    {
        public int Reads { get; set; }

        public IConfigurationProvider Build(IConfigurationBuilder builder) => new Provider(this, values);

        private sealed class Provider(CountingConfigurationSource source, IDictionary<string, string?> values) : ConfigurationProvider
        {
            public override void Load() => Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);

            public override bool TryGet(string key, out string? value)
            {
                if (key.StartsWith("Database", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase))
                    source.Reads++;
                return base.TryGet(key, out value);
            }
        }
    }
}
