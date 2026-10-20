using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Infrastructure.Configuration;
using System.Text.RegularExpressions;

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
        settings.BufferedNetworkTimeoutSeconds.Should().Be(90);
        settings.CompletionTimeout.Should().Be(TimeSpan.FromSeconds(185));
        settings.StreamingCompletionTimeout.Should().Be(TimeSpan.FromSeconds(75));
    }

    [Theory]
    [InlineData(15, 90, 0)]
    [InlineData(30, 120, 5)]
    public void CompletionTimeout_IsIndependentOfNetworkTimeoutsAndRetryCount(int streamingSeconds, int bufferedSeconds, int retries)
    {
        var settings = new AiSettings
        {
            NetworkTimeoutSeconds = streamingSeconds,
            BufferedNetworkTimeoutSeconds = bufferedSeconds,
            MaxRetries = retries
        };

        settings.CompletionTimeout.Should().Be(TimeSpan.FromSeconds(185));
    }

    [Fact]
    public void RenderEnvironmentGroups_UseConfiguredPrimaryAndSubTaskModels()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Orbit.slnx")))
            directory = directory.Parent;
        directory.Should().NotBeNull();
        var terraform = File.ReadAllText(Path.Combine(directory!.FullName, "infra", "configuration.tf"));

        var production = Regex.Match(terraform, @"(?ms)^  production_api_values\s*=\s*\{(?<values>.*?)^  \}");
        production.Success.Should().BeTrue();
        production.Groups["values"].Value.Should().MatchRegex("(?m)^\\s*AI__Model\\s*=\\s*\"gpt-6\\.1-sol\"\\s*$");
        production.Groups["values"].Value.Should().MatchRegex("(?m)^\\s*AI__SubTaskModel\\s*=\\s*\"gpt-6-luna\"\\s*$");

        var staging = Regex.Match(terraform, @"(?ms)^  staging_api_values\s*=\s*merge\(local\.production_api_values,\s*\{(?<overrides>.*?)^  \}\)");
        staging.Success.Should().BeTrue();
        staging.Groups["overrides"].Value.Should().NotMatchRegex(@"(?m)^\s*AI__(Model|SubTaskModel)\s*=");
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
        var pricing = ai.GetProperty("Pricing");
        pricing.GetProperty("gpt-6-luna").GetProperty("CacheWritePerMillionUsd").GetDecimal().Should().Be(0.125m);
        pricing.GetProperty("gpt-6.1-sol").GetProperty("CacheWritePerMillionUsd").GetDecimal().Should().Be(2.50m);
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
