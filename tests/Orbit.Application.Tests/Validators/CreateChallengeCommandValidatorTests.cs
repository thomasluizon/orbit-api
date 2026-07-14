using FluentValidation.TestHelper;
using Orbit.Application.Challenges.Commands;
using Orbit.Application.Challenges.Validators;
using Orbit.Application.Common;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Validators;

public class CreateChallengeCommandValidatorTests
{
    private readonly CreateChallengeCommandValidator _validator = new();
    private static readonly DateOnly Start = new(2026, 3, 1);

    private static CreateChallengeCommand ValidCoopGoal() => new(
        UserId: Guid.NewGuid(),
        Type: ChallengeType.CoopGoal,
        Title: "March miles",
        Description: null,
        TargetCount: 30,
        PeriodStartUtc: Start,
        PeriodEndUtc: Start.AddDays(30),
        LinkedHabitIds: new[] { Guid.NewGuid() },
        InvitedFriendUserIds: Array.Empty<Guid>());

    private static CreateChallengeCommand ValidStreakTogether() => new(
        UserId: Guid.NewGuid(),
        Type: ChallengeType.StreakTogether,
        Title: "Streak buddies",
        Description: "Keep each other honest",
        TargetCount: null,
        PeriodStartUtc: Start,
        PeriodEndUtc: null,
        LinkedHabitIds: new[] { Guid.NewGuid() },
        InvitedFriendUserIds: new[] { Guid.NewGuid() });

    [Fact]
    public void ValidCoopGoal_NoErrors() =>
        _validator.TestValidate(ValidCoopGoal()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void ValidStreakTogether_NoErrors() =>
        _validator.TestValidate(ValidStreakTogether()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void EmptyUserId_HasError()
    {
        var command = ValidCoopGoal() with { UserId = Guid.Empty };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void EmptyTitle_HasError()
    {
        var command = ValidCoopGoal() with { Title = "" };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void TitleTooLong_HasError()
    {
        var command = ValidCoopGoal() with { Title = new string('a', AppConstants.MaxChallengeTitleLength + 1) };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public void CoopGoal_WithoutTargetCount_HasError()
    {
        var command = ValidCoopGoal() with { TargetCount = null };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.TargetCount);
    }

    [Fact]
    public void CoopGoal_WithZeroTargetCount_HasError()
    {
        var command = ValidCoopGoal() with { TargetCount = 0 };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.TargetCount);
    }

    [Fact]
    public void CoopGoal_WithoutEndDate_HasError()
    {
        var command = ValidCoopGoal() with { PeriodEndUtc = null };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.PeriodEndUtc);
    }

    [Fact]
    public void StreakTogether_WithTargetCount_HasError()
    {
        var command = ValidStreakTogether() with { TargetCount = 5 };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.TargetCount);
    }

    [Fact]
    public void EndDateBeforeStartDate_HasError()
    {
        var command = ValidCoopGoal() with { PeriodEndUtc = Start.AddDays(-1) };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.PeriodEndUtc);
    }

    [Fact]
    public void NoLinkedHabits_HasError()
    {
        var command = ValidCoopGoal() with { LinkedHabitIds = Array.Empty<Guid>() };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.LinkedHabitIds);
    }

    [Fact]
    public void EmptyLinkedHabitId_HasError()
    {
        var command = ValidCoopGoal() with { LinkedHabitIds = new[] { Guid.Empty } };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor("LinkedHabitIds[0]");
    }

    [Fact]
    public void TooManyLinkedHabits_HasError()
    {
        var habits = Enumerable.Range(0, AppConstants.MaxHabitsPerChallengeParticipant + 1)
            .Select(_ => Guid.NewGuid()).ToArray();
        var command = ValidCoopGoal() with { LinkedHabitIds = habits };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.LinkedHabitIds);
    }

    [Fact]
    public void TooManyInvitedFriends_HasError()
    {
        var friends = Enumerable.Range(0, AppConstants.MaxChallengeParticipants)
            .Select(_ => Guid.NewGuid()).ToArray();
        var command = ValidCoopGoal() with { InvitedFriendUserIds = friends };

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(x => x.InvitedFriendUserIds);
    }
}
