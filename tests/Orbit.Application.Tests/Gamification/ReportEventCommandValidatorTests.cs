using FluentAssertions;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Validators;

namespace Orbit.Application.Tests.Gamification;

public class ReportEventCommandValidatorTests
{
    private readonly ReportEventCommandValidator _validator = new();
    private static readonly Guid UserId = Guid.NewGuid();

    [Theory]
    [InlineData(AchievementEventMap.CardShared)]
    [InlineData(AchievementEventMap.WrappedViewed)]
    public void Validate_KnownKey_Passes(string eventKey)
    {
        var result = _validator.Validate(new ReportEventCommand(UserId, eventKey));

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown_key")]
    [InlineData("CARD_SHARED")]
    public void Validate_UnknownOrEmptyKey_Fails(string eventKey)
    {
        var result = _validator.Validate(new ReportEventCommand(UserId, eventKey));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_EmptyUserId_Fails()
    {
        var result = _validator.Validate(new ReportEventCommand(Guid.Empty, AchievementEventMap.CardShared));

        result.IsValid.Should().BeFalse();
    }
}
