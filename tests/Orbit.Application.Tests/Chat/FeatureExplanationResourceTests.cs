using System.Reflection;
using FluentAssertions;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Chat;

public class FeatureExplanationResourceTests
{
    private const string Prefix = "Orbit.Application.Chat.Content.FeatureExplanations.";

    private static readonly string[] ExpectedKeys =
    [
        "streaks",
        "frequencies",
        "gamification",
        "paygate",
        "schedule-math",
        "freezes",
        "notifications",
        "ai-memory",
    ];

    private static Assembly ApplicationAssembly => typeof(AppConstants).Assembly;

    [Fact]
    public void EmbedsExactlyTheEightFeatureExplanationFiles()
    {
        var names = ApplicationAssembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .ToList();

        var expectedNames = ExpectedKeys.Select(key => $"{Prefix}{key}.md");

        names.Should().BeEquivalentTo(expectedNames);
    }

    [Theory]
    [InlineData("streaks")]
    [InlineData("frequencies")]
    [InlineData("gamification")]
    [InlineData("paygate")]
    [InlineData("schedule-math")]
    [InlineData("freezes")]
    [InlineData("notifications")]
    [InlineData("ai-memory")]
    public void EachResourceLoadsWithFrontmatterKeyMatchingItsFilename(string key)
    {
        using var stream = ApplicationAssembly.GetManifestResourceStream($"{Prefix}{key}.md");
        stream.Should().NotBeNull();

        using var reader = new StreamReader(stream!);
        var content = reader.ReadToEnd();

        content.Should().StartWith("---");
        content.Should().Contain($"key: {key}");
    }
}
