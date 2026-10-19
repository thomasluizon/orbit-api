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
        settings.Model.Should().Be("gpt-6.1-sol");
        settings.BaseUrl.Should().Be("https://api.openai.com/v1");
    }

    [Fact]
    public void Defaults_RouteMechanicalSubTasksToLuna_WhenConfigOmitsThem()
    {
        var settings = new AiSettings();

        settings.SubTaskModel.Should().Be("gpt-6-luna");
    }

    [Fact]
    public void Defaults_AllowBufferedReasoningWithoutExtendingStreamingNetworkTimeout()
    {
        var settings = new AiSettings();

        settings.NetworkTimeoutSeconds.Should().Be(15);
        settings.CompletionTimeout.Should().Be(TimeSpan.FromSeconds(450));
    }

    [Fact]
    public void RenderEnvironmentGroups_UseConfiguredPrimaryAndSubTaskModels()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Orbit.slnx")))
            directory = directory.Parent;
        directory.Should().NotBeNull();
        var terraform = File.ReadAllText(Path.Combine(directory!.FullName, "infra", "configuration.tf"));

        terraform.Should().MatchRegex("AI__Model\\s*=\\s*\"gpt-6.1-sol\"");
        terraform.Should().MatchRegex("AI__SubTaskModel\\s*=\\s*\"gpt-6-luna\"");
        terraform.Should().Contain("staging_api_values = merge(local.production_api_values, {");
    }
    [Fact]
    public void ConfiguredAndDefaultModels_HavePricingRows()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Orbit.slnx")))
            directory = directory.Parent;
        directory.Should().NotBeNull();
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(directory!.FullName, "src", "Orbit.Api", "appsettings.json")));
        var ai = document.RootElement.GetProperty("AI");
        var defaults = new AiSettings();
        foreach (var model in new[] { defaults.Model, defaults.SubTaskModel, ai.GetProperty("Model").GetString(), ai.GetProperty("SubTaskModel").GetString() })
        {
            ai.GetProperty("Pricing").TryGetProperty(model!, out var price).Should().BeTrue($"{model} must record a nonzero cost");
            price.GetProperty("InputPerMillionUsd").GetDecimal().Should().BeGreaterThan(0);
            price.GetProperty("OutputPerMillionUsd").GetDecimal().Should().BeGreaterThan(0);
        }
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
