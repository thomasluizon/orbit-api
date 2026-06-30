using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class ChallengeTests
{
    private static readonly Guid CreatorId = Guid.NewGuid();
    private static readonly DateOnly PeriodStart = new(2026, 3, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 3, 31);

    private static CreateChallengeParams CoopGoalParams(int? targetCount = 30) =>
        new(CreatorId, ChallengeType.CoopGoal, "March Push-ups", "Together", targetCount, PeriodStart, PeriodEnd, "ABC23456");

    private static CreateChallengeParams StreakParams(int? targetCount = null, DateOnly? periodEnd = null) =>
        new(CreatorId, ChallengeType.StreakTogether, "Daily Reading", null, targetCount, PeriodStart, periodEnd, "XYZ78999");

    [Fact]
    public void Create_ValidCoopGoal_ReturnsActiveChallenge()
    {
        var result = Challenge.Create(CoopGoalParams());

        result.IsSuccess.Should().BeTrue();
        result.Value.Type.Should().Be(ChallengeType.CoopGoal);
        result.Value.Status.Should().Be(ChallengeStatus.Active);
        result.Value.TargetCount.Should().Be(30);
        result.Value.JoinCode.Should().Be("ABC23456");
        result.Value.Participants.Should().BeEmpty();
    }

    [Fact]
    public void Create_CoopGoalWithoutTarget_Fails()
    {
        var result = Challenge.Create(CoopGoalParams(targetCount: null));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("CHALLENGE_TARGET_REQUIRED");
    }

    [Fact]
    public void Create_CoopGoalWithNonPositiveTarget_Fails()
    {
        var result = Challenge.Create(CoopGoalParams(targetCount: 0));

        result.ErrorCode.Should().Be("CHALLENGE_TARGET_REQUIRED");
    }

    [Fact]
    public void Create_StreakWithTarget_Fails()
    {
        var result = Challenge.Create(StreakParams(targetCount: 10));

        result.ErrorCode.Should().Be("CHALLENGE_TARGET_NOT_ALLOWED");
    }

    [Fact]
    public void Create_StreakWithoutTarget_Succeeds()
    {
        var result = Challenge.Create(StreakParams());

        result.IsSuccess.Should().BeTrue();
        result.Value.TargetCount.Should().BeNull();
    }

    [Fact]
    public void Create_EndBeforeStart_Fails()
    {
        var invalid = CoopGoalParams() with { PeriodEndUtc = PeriodStart.AddDays(-1) };

        var result = Challenge.Create(invalid);

        result.ErrorCode.Should().Be("CHALLENGE_PERIOD_INVALID");
    }

    [Fact]
    public void Create_BlankTitle_Fails()
    {
        var invalid = CoopGoalParams() with { Title = "  " };

        var result = Challenge.Create(invalid);

        result.ErrorCode.Should().Be("TITLE_REQUIRED");
    }

    [Fact]
    public void AddParticipant_LinksOwnHabitsAndDeduplicates()
    {
        var challenge = Challenge.Create(CoopGoalParams()).Value;
        var habitId = Guid.NewGuid();

        var participant = challenge.AddParticipant(CreatorId, [habitId, habitId]);

        challenge.Participants.Should().ContainSingle();
        participant.UserId.Should().Be(CreatorId);
        participant.IsActive.Should().BeTrue();
        participant.LinkedHabits.Should().ContainSingle(h => h.HabitId == habitId);
    }

    [Fact]
    public void TryLeave_ActiveParticipant_MarksLeft()
    {
        var challenge = Challenge.Create(CoopGoalParams()).Value;
        challenge.AddParticipant(CreatorId, [Guid.NewGuid()]);

        var left = challenge.TryLeave(CreatorId);

        left.Should().BeTrue();
        challenge.Participants.Single().IsActive.Should().BeFalse();
        challenge.GetActiveParticipants().Should().BeEmpty();
    }

    [Fact]
    public void TryLeave_NonParticipant_ReturnsFalse()
    {
        var challenge = Challenge.Create(CoopGoalParams()).Value;
        challenge.AddParticipant(CreatorId, [Guid.NewGuid()]);

        challenge.TryLeave(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void TryLeave_AlreadyLeft_ReturnsFalse()
    {
        var challenge = Challenge.Create(CoopGoalParams()).Value;
        challenge.AddParticipant(CreatorId, [Guid.NewGuid()]);
        challenge.TryLeave(CreatorId);

        challenge.TryLeave(CreatorId).Should().BeFalse();
    }

    [Fact]
    public void MarkCompleted_TransitionsOnceThenIsIdempotent()
    {
        var challenge = Challenge.Create(CoopGoalParams()).Value;

        var first = challenge.MarkCompleted();
        var second = challenge.MarkCompleted();

        first.Should().BeTrue();
        second.Should().BeFalse();
        challenge.Status.Should().Be(ChallengeStatus.Completed);
        challenge.CompletedAtUtc.Should().NotBeNull();
    }
}
