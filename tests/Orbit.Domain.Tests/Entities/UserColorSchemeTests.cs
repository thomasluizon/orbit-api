using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class UserColorSchemeTests
{
    private static User CreateUser() => User.Create("Test User", "test@example.com").Value;

    [Theory]
    [InlineData("purple")]
    [InlineData("blue")]
    [InlineData("green")]
    [InlineData("rose")]
    [InlineData("orange")]
    [InlineData("cyan")]
    public void SetColorScheme_HistoricalKey_SucceedsAndStoresTheGrantedAccent(string colorScheme)
    {
        var user = CreateUser();

        var result = user.SetColorScheme(colorScheme);

        result.IsSuccess.Should().BeTrue();
        user.ColorScheme.Should().Be(ColorSchemes.Granted);
    }

    [Fact]
    public void SetColorScheme_Null_ClearsTheStoredPreference()
    {
        var user = CreateUser();
        user.SetColorScheme("rose").IsSuccess.Should().BeTrue();

        var result = user.SetColorScheme(null);

        result.IsSuccess.Should().BeTrue();
        user.ColorScheme.Should().BeNull();
    }

    [Fact]
    public void SetColorScheme_UnknownKey_FailsAndKeepsTheStoredValue()
    {
        var user = CreateUser();
        user.SetColorScheme("blue").IsSuccess.Should().BeTrue();

        var result = user.SetColorScheme("magenta");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("INVALID_COLOR_SCHEME");
        user.ColorScheme.Should().Be(ColorSchemes.Granted);
    }

    [Fact]
    public void AcceptedValues_CoverTheSixHistoricalKeysAndNothingElse()
    {
        ColorSchemes.AcceptedValues.Should().BeEquivalentTo(
            ["purple", "blue", "green", "rose", "orange", "cyan"]);
        ColorSchemes.AcceptedValues.Should().Contain(ColorSchemes.Granted);
    }
}
