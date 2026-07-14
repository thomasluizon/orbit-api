using FluentValidation.TestHelper;
using Orbit.Application.Challenges.Commands;
using Orbit.Application.Challenges.Validators;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Validators;

public class JoinChallengeCommandValidatorTests
{
    private readonly JoinChallengeCommandValidator _validator = new();

    private static JoinChallengeCommand Valid() => new(
        UserId: Guid.NewGuid(),
        Code: "ABCD2345",
        LinkedHabitIds: new[] { Guid.NewGuid() });

    [Fact]
    public void Valid_NoErrors() =>
        _validator.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void EmptyUserId_HasError() =>
        _validator.TestValidate(Valid() with { UserId = Guid.Empty }).ShouldHaveValidationErrorFor(x => x.UserId);

    [Fact]
    public void EmptyCode_HasError() =>
        _validator.TestValidate(Valid() with { Code = "" }).ShouldHaveValidationErrorFor(x => x.Code);

    [Fact]
    public void CodeTooLong_HasError() =>
        _validator.TestValidate(Valid() with { Code = new string('A', 17) }).ShouldHaveValidationErrorFor(x => x.Code);

    [Fact]
    public void NoLinkedHabits_HasError() =>
        _validator.TestValidate(Valid() with { LinkedHabitIds = Array.Empty<Guid>() })
            .ShouldHaveValidationErrorFor(x => x.LinkedHabitIds);

    [Fact]
    public void EmptyLinkedHabitId_HasError() =>
        _validator.TestValidate(Valid() with { LinkedHabitIds = new[] { Guid.Empty } })
            .ShouldHaveValidationErrorFor("LinkedHabitIds[0]");

    [Fact]
    public void TooManyLinkedHabits_HasError()
    {
        var habits = Enumerable.Range(0, AppConstants.MaxHabitsPerChallengeParticipant + 1)
            .Select(_ => Guid.NewGuid()).ToArray();

        _validator.TestValidate(Valid() with { LinkedHabitIds = habits })
            .ShouldHaveValidationErrorFor(x => x.LinkedHabitIds);
    }
}
