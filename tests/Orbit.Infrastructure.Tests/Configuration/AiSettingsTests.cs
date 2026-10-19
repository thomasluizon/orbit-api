using FluentAssertions;
using Orbit.Application.Chat;
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
    [Theory]
    [InlineData(15, 120)]
    [InlineData(90, 120)]
    [InlineData(15, 240)]
    public void BlockAnswerTimeout_CoversInitialCompletionEachContinuationAndSummary(int networkSeconds, int transactionSeconds)
    {
        var ai = new AiSettings { NetworkTimeoutSeconds = networkSeconds };
        var database = new DatabaseConnectionSettings { TransactionTimeoutSeconds = transactionSeconds };
        var completion = ai.CompletionTimeout;
        var transaction = TimeSpan.FromSeconds(database.TransactionTimeoutSeconds);

        var lifetime = ChatTurnLimits.BlockAnswerTimeout(completion, transaction);

        lifetime.Should().Be((1 + 2 * ChatTurnLimits.MaxToolIterations) * (completion + transaction));
        lifetime.Should().BeGreaterThan(TimeSpan.FromSeconds(135));
    }

}
