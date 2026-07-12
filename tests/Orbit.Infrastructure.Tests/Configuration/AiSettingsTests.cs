using FluentAssertions;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Tests.Configuration;

public class AiSettingsTests
{
    [Fact]
    public void Defaults_KeepTightTimeoutAndBoundedRetries_WhenConfigOmitsThem()
    {
        var settings = new AiSettings();

        settings.NetworkTimeoutSeconds.Should().Be(15);
        settings.MaxRetries.Should().Be(2);
        settings.Model.Should().Be("gpt-4.1-mini");
        settings.BaseUrl.Should().Be("https://api.openai.com/v1");
    }

    [Fact]
    public void Defaults_GiveBatchTransfersMoreHeadroomThanChatTurns_WhenConfigOmitsThem()
    {
        var settings = new AiSettings();

        settings.BatchNetworkTimeoutSeconds.Should().Be(120);
        settings.BatchNetworkTimeoutSeconds.Should().BeGreaterThan(settings.NetworkTimeoutSeconds);
    }

    [Fact]
    public void Defaults_RouteMechanicalSubTasksToNano_WhenConfigOmitsThem()
    {
        var settings = new AiSettings();

        settings.SubTaskModel.Should().Be("gpt-5.4-nano");
    }
}
