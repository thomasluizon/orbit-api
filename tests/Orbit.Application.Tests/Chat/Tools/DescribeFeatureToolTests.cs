using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat.FeatureExplanations;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class DescribeFeatureToolTests
{
    private const string ResourcePrefix = "Orbit.Application.Chat.Content.FeatureExplanations.";

    private static readonly string[] FeatureKeyEnum =
    [
        "ai-memory",
        "freezes",
        "frequencies",
        "gamification",
        "notifications",
        "paygate",
        "schedule-math",
        "streaks",
    ];

    private readonly FeatureExplanationService _service = new();

    [Fact]
    public void FeatureKeyEnum_MatchesEmbeddedResourceKeys()
    {
        var embeddedKeys = typeof(AppConstants).Assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(name => name[ResourcePrefix.Length..^".md".Length])
            .ToList();

        embeddedKeys.Should().BeEquivalentTo(FeatureKeyEnum);
        _service.Keys.Should().BeEquivalentTo(FeatureKeyEnum);
    }

    [Fact]
    public void EnumSchema_ExposesEveryFeatureKey()
    {
        var schema = JsonSerializer.Serialize(new DescribeFeatureTool(_service).GetParameterSchema());

        schema.Should().Contain("feature_key");
        foreach (var key in FeatureKeyEnum)
            schema.Should().Contain(key);
    }

    [Theory]
    [InlineData("ai-memory")]
    [InlineData("freezes")]
    [InlineData("frequencies")]
    [InlineData("gamification")]
    [InlineData("notifications")]
    [InlineData("paygate")]
    [InlineData("schedule-math")]
    [InlineData("streaks")]
    public void EveryEnumKey_ResolvesToAFullyPopulatedExplanation(string key)
    {
        var explanation = _service.Get(key);

        explanation.Should().NotBeNull();
        explanation!.Key.Should().Be(key);
        explanation.DisplayName.Should().NotBeNullOrWhiteSpace();
        explanation.Body.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Metadata_IsReadOnlyDescribeFeature()
    {
        var tool = new DescribeFeatureTool(_service);

        tool.Name.Should().Be("describe_feature");
        tool.IsReadOnly.Should().BeTrue();
        tool.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExecuteAsync_ValidKey_ReturnsMarkdownAndMetadataPayload()
    {
        var tool = new DescribeFeatureTool(_service);

        var result = await tool.ExecuteAsync(Args("freezes"), Guid.NewGuid(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Payload.Should().NotBeNull();

        var payload = JsonSerializer.Serialize(result.Payload);
        payload.Should().Contain("markdown");
        payload.Should().Contain("related_surfaces");
        payload.Should().Contain("Streak Freezes");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownKey_ReturnsError()
    {
        var tool = new DescribeFeatureTool(_service);

        var result = await tool.ExecuteAsync(Args("does-not-exist"), Guid.NewGuid(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    private static JsonElement Args(string featureKey) =>
        JsonDocument.Parse($$"""{"feature_key":"{{featureKey}}"}""").RootElement;
}
