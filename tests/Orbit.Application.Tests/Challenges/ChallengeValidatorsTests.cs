using FluentAssertions;
using Orbit.Application.Challenges.Commands;
using Orbit.Application.Challenges.Queries;
using Orbit.Application.Challenges.Validators;

namespace Orbit.Application.Tests.Challenges;

public class ChallengeValidatorsTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid ChallengeId = Guid.NewGuid();

    private static IReadOnlyList<Guid> Habits(int count) =>
        Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetUserChallenges_RequiresUserId(bool hasUserId)
    {
        var validator = new GetUserChallengesQueryValidator();
        var result = validator.Validate(new GetUserChallengesQuery(hasUserId ? UserId : Guid.Empty));
        result.IsValid.Should().Be(hasUserId);
    }

    [Fact]
    public void SetHabits_AcceptsValidCommand()
    {
        var validator = new SetChallengeHabitsCommandValidator();
        var result = validator.Validate(new SetChallengeHabitsCommand(UserId, ChallengeId, Habits(1)));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void SetHabits_RejectsEmptyUserId()
    {
        var validator = new SetChallengeHabitsCommandValidator();
        var result = validator.Validate(new SetChallengeHabitsCommand(Guid.Empty, ChallengeId, Habits(1)));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void SetHabits_RejectsEmptyChallengeId()
    {
        var validator = new SetChallengeHabitsCommandValidator();
        var result = validator.Validate(new SetChallengeHabitsCommand(UserId, Guid.Empty, Habits(1)));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void SetHabits_RejectsEmptyHabitIds()
    {
        var validator = new SetChallengeHabitsCommandValidator();
        var result = validator.Validate(new SetChallengeHabitsCommand(UserId, ChallengeId, []));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void SetHabits_RejectsTooManyHabitIds()
    {
        var validator = new SetChallengeHabitsCommandValidator();
        var result = validator.Validate(new SetChallengeHabitsCommand(UserId, ChallengeId, Habits(21)));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void SetHabits_RejectsEmptyGuidHabitId()
    {
        var validator = new SetChallengeHabitsCommandValidator();
        var result = validator.Validate(new SetChallengeHabitsCommand(UserId, ChallengeId, new[] { Guid.Empty }));
        result.IsValid.Should().BeFalse();
    }
}
