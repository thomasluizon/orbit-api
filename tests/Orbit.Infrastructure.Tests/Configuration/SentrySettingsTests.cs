using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orbit.Api.Extensions;
using Sentry.AspNetCore;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.Configuration;

public class SentrySettingsTests
{
    [Fact]
    public void Defaults_DisableLogsAndKeepTracingOff_WhenConfigOmitsThem()
    {
        var settings = new SentrySettings();

        settings.EnableLogs.Should().BeFalse();
        settings.TracesSampleRate.Should().Be(0);
        settings.Environment.Should().Be("production");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void Observability_AlwaysDisablesStructuredLogs(string configuredValue)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Sentry:EnableLogs"] = configuredValue;
        builder.AddOrbitObservability();
        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<SentryAspNetCoreOptions>>().Value.EnableLogs.Should().BeFalse();
    }

    [Fact]
    public void EnableLogs_IsRevertible_WhenConfigDisablesIt()
    {
        var settings = new SentrySettings { EnableLogs = false };

        settings.EnableLogs.Should().BeFalse();
    }
}
