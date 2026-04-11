using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class AppFeatureFlagTests
{
    [Fact]
    public void Create_AllParameters_SetsProperties()
    {
        var flag = AppFeatureFlag.Create("ai_chat", true, "Pro", "Enable AI chat feature");

        flag.Key.Should().Be("ai_chat");
        flag.Enabled.Should().BeTrue();
        flag.PlanRequirement.Should().Be("Pro");
        flag.Description.Should().Be("Enable AI chat feature");
        flag.UpdatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Create_MinimalParameters_SetsDefaults()
    {
        var flag = AppFeatureFlag.Create("basic_feature", false);

        flag.Key.Should().Be("basic_feature");
        flag.Enabled.Should().BeFalse();
        flag.PlanRequirement.Should().BeNull();
        flag.Description.Should().BeNull();
    }

    [Fact]
    public void SetEnabled_True_EnablesFlag()
    {
        var flag = AppFeatureFlag.Create("feature", false);
        flag.SetEnabled(true);
        flag.Enabled.Should().BeTrue();
    }

    [Fact]
    public void SetEnabled_False_DisablesFlag()
    {
        var flag = AppFeatureFlag.Create("feature", true);
        flag.SetEnabled(false);
        flag.Enabled.Should().BeFalse();
    }

    [Fact]
    public void SetEnabled_UpdatesTimestamp()
    {
        var flag = AppFeatureFlag.Create("feature", false);
        var originalTimestamp = flag.UpdatedAtUtc;
        flag.SetEnabled(true);
        flag.UpdatedAtUtc.Should().BeOnOrAfter(originalTimestamp);
    }
}
