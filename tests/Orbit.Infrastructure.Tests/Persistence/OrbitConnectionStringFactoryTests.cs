using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class OrbitConnectionStringFactoryTests
{
    [Fact]
    public void ForRequestPath_AppliesEfPoolCap_AndPreservesEndpointAndParams()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] =
                "Host=db.example.com;Port=6543;Database=postgres;Username=u;Password=p;No Reset On Close=true",
            ["Database:EfMaxPoolSize"] = "10"
        });

        var result = new NpgsqlConnectionStringBuilder(OrbitConnectionStringFactory.ForRequestPath(configuration));

        result.MaxPoolSize.Should().Be(10);
        result.MinPoolSize.Should().Be(0);
        result.Host.Should().Be("db.example.com");
        result.Port.Should().Be(6543);
        result.NoResetOnClose.Should().BeTrue();
    }

    [Fact]
    public void ForRequestPath_OverridesPoolSizePresentInConnectionString()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=h;Maximum Pool Size=200",
            ["Database:EfMaxPoolSize"] = "12"
        });

        new NpgsqlConnectionStringBuilder(OrbitConnectionStringFactory.ForRequestPath(configuration))
            .MaxPoolSize.Should().Be(12);
    }

    [Fact]
    public void ForSession_PrefersSessionConnection_OverDefault()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=transaction;Port=6543",
            ["ConnectionStrings:SessionConnection"] = "Host=session;Port=5432",
            ["Database:SessionMaxPoolSize"] = "5"
        });

        var result = new NpgsqlConnectionStringBuilder(OrbitConnectionStringFactory.ForSession(configuration));

        result.Host.Should().Be("session");
        result.Port.Should().Be(5432);
        result.MaxPoolSize.Should().Be(5);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ForSession_FallsBackToDefault_WhenSessionConnectionBlankOrMissing(string? sessionValue)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=default;Port=5432"
        };
        if (sessionValue is not null)
            values["ConnectionStrings:SessionConnection"] = sessionValue;

        var result = new NpgsqlConnectionStringBuilder(OrbitConnectionStringFactory.ForSession(BuildConfiguration(values)));

        result.Host.Should().Be("default");
    }

    [Fact]
    public void UsesDefaultCaps_WhenDatabaseSectionAbsent()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=h;Port=6543",
            ["ConnectionStrings:SessionConnection"] = "Host=h2;Port=5432"
        });

        new NpgsqlConnectionStringBuilder(OrbitConnectionStringFactory.ForRequestPath(configuration))
            .MaxPoolSize.Should().Be(15);
        new NpgsqlConnectionStringBuilder(OrbitConnectionStringFactory.ForSession(configuration))
            .MaxPoolSize.Should().Be(5);
    }

    [Fact]
    public void ReturnsEmpty_WhenNoConnectionStringConfigured()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>());

        OrbitConnectionStringFactory.ForRequestPath(configuration).Should().BeEmpty();
        OrbitConnectionStringFactory.ForSession(configuration).Should().BeEmpty();
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
