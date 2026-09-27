using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Orbit.Api.Extensions;

namespace Orbit.Infrastructure.Tests.Extensions;

public class WebApplicationExtensionsMigrationTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task MigrateDatabaseIfEnabledAsync_RespectsSetting(bool enabled, int expectedCalls)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = enabled.ToString(),
                ["ConnectionStrings:SessionConnection"] = "Host=session;Database=orbit;Username=test;Password=test"
            })
            .Build();
        var calls = 0;

        await WebApplicationExtensions.MigrateDatabaseIfEnabledAsync(configuration, (connection, settings) =>
        {
            connection.Should().Contain("Host=session");
            settings.MigrateOnStartup.Should().BeTrue();
            calls++;
            return Task.CompletedTask;
        });

        calls.Should().Be(expectedCalls);
    }
}
