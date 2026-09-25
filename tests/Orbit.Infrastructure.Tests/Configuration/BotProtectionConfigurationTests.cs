using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Orbit.Api.Extensions;

namespace Orbit.Infrastructure.Tests.Configuration;

public class BotProtectionConfigurationTests
{
    [Fact]
    public void EnabledWithoutSecret_FailsAtRegistration()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BotProtection:Enabled"] = "true",
            ["BotProtection:SecretKey"] = " "
        });

        var action = () => ServiceCollectionExtensions.AddBotProtection(builder, TimeSpan.FromSeconds(5));

        action.Should().Throw<InvalidOperationException>().WithMessage("*BotProtection:SecretKey*");
    }

    [Fact]
    public void DisabledWithoutSecret_RegistersSuccessfully()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BotProtection:Enabled"] = "false",
            ["BotProtection:SecretKey"] = ""
        });

        var action = () => ServiceCollectionExtensions.AddBotProtection(builder, TimeSpan.FromSeconds(5));

        action.Should().NotThrow();
    }
}
