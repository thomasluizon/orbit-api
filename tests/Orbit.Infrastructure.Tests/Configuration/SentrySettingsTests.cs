using FluentAssertions;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.Configuration;

public class SentrySettingsTests
{
    [Fact]
    public void Defaults_EnableLogsAndKeepTracingOff_WhenConfigOmitsThem()
    {
        var settings = new SentrySettings();

        settings.EnableLogs.Should().BeTrue();
        settings.TracesSampleRate.Should().Be(0);
        settings.Environment.Should().Be("production");
    }

    [Fact]
    public void EnableLogs_IsRevertible_WhenConfigDisablesIt()
    {
        var settings = new SentrySettings { EnableLogs = false };

        settings.EnableLogs.Should().BeFalse();
    }
}
