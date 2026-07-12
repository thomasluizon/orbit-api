using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.Configuration;

public class DatabaseConnectionSettingsTests
{
    [Fact]
    public void Defaults_KeepSessionPoolSmallerThanRequestPool_SoSessionPoolerBackendsStayBounded()
    {
        var settings = new DatabaseConnectionSettings();

        settings.EfMaxPoolSize.Should().Be(15);
        settings.SessionMaxPoolSize.Should().Be(5);
        settings.SessionMaxPoolSize.Should().BeLessThan(settings.EfMaxPoolSize);
    }

    [Fact]
    public void Defaults_KeepTwoInstanceSessionOverlapUnderSupabaseUsableBackends()
    {
        var settings = new DatabaseConnectionSettings();

        (settings.SessionMaxPoolSize * 2).Should().BeLessThan(57);
    }

    [Fact]
    public void Defaults_GiveMigrationsMoreCommandHeadroomThanRequestPath()
    {
        var settings = new DatabaseConnectionSettings();

        settings.CommandTimeoutSeconds.Should().Be(60);
        settings.MigrationCommandTimeoutSeconds.Should().Be(180);
        settings.MigrationCommandTimeoutSeconds.Should().BeGreaterThan(settings.CommandTimeoutSeconds);
    }

    [Fact]
    public void From_BindsEveryConfiguredValue()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Database:EfMaxPoolSize"] = "20",
            ["Database:SessionMaxPoolSize"] = "6",
            ["Database:CommandTimeoutSeconds"] = "90",
            ["Database:MigrationCommandTimeoutSeconds"] = "240"
        });

        var settings = DatabaseConnectionSettings.From(configuration);

        settings.EfMaxPoolSize.Should().Be(20);
        settings.SessionMaxPoolSize.Should().Be(6);
        settings.CommandTimeoutSeconds.Should().Be(90);
        settings.MigrationCommandTimeoutSeconds.Should().Be(240);
    }

    [Fact]
    public void From_ReturnsDefaults_WhenSectionAbsent()
    {
        var settings = DatabaseConnectionSettings.From(BuildConfiguration(new Dictionary<string, string?>()));

        settings.EfMaxPoolSize.Should().Be(15);
        settings.SessionMaxPoolSize.Should().Be(5);
        settings.CommandTimeoutSeconds.Should().Be(60);
        settings.MigrationCommandTimeoutSeconds.Should().Be(180);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
