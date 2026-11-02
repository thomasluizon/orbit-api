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
    public void Settings_DoNotExposeUnsupportedStructuredLogsSwitch()
    {
        typeof(SentrySettings).GetProperty("EnableLogs").Should().BeNull();
    }

    [Fact]
    public void Defaults_KeepTracingOff_WhenConfigOmitsIt()
    {
        var settings = new SentrySettings();

        settings.TracesSampleRate.Should().Be(0);
        settings.Environment.Should().Be("production");
    }

    [Fact]
    public void Observability_AlwaysDisablesStructuredLogs()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddOrbitObservability();
        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<SentryAspNetCoreOptions>>().Value.EnableLogs.Should().BeFalse();
    }
}
