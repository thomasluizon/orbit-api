using FluentAssertions;
using Orbit.Application.Accountability.Commands;
using Orbit.Application.Accountability.Queries;
using Orbit.Application.Accountability.Validators;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Accountability;

public class AccountabilityValidatorsTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid PairId = Guid.NewGuid();

    private static List<Guid> Habits(int count) =>
        Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();

    [Fact]
    public void Invite_AcceptsValidCommand()
    {
        var validator = new InviteAccountabilityBuddyCommandValidator();
        var result = validator.Validate(new InviteAccountabilityBuddyCommand(
            UserId, Guid.NewGuid(), AccountabilityCadence.Weekly, Habits(1)));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Invite_RejectsPairingSelf()
    {
        var validator = new InviteAccountabilityBuddyCommandValidator();
        var result = validator.Validate(new InviteAccountabilityBuddyCommand(
            UserId, UserId, AccountabilityCadence.Daily, Habits(1)));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Invite_RejectsInvalidCadence()
    {
        var validator = new InviteAccountabilityBuddyCommandValidator();
        var result = validator.Validate(new InviteAccountabilityBuddyCommand(
            UserId, Guid.NewGuid(), (AccountabilityCadence)999, Habits(1)));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Invite_RejectsEmptyHabitIds()
    {
        var validator = new InviteAccountabilityBuddyCommandValidator();
        var result = validator.Validate(new InviteAccountabilityBuddyCommand(
            UserId, Guid.NewGuid(), AccountabilityCadence.Daily, []));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Invite_RejectsTooManyHabitIds()
    {
        var validator = new InviteAccountabilityBuddyCommandValidator();
        var result = validator.Validate(new InviteAccountabilityBuddyCommand(
            UserId, Guid.NewGuid(), AccountabilityCadence.Daily, Habits(11)));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Invite_RejectsDuplicateHabitIds()
    {
        var habitId = Guid.NewGuid();
        var validator = new InviteAccountabilityBuddyCommandValidator();
        var result = validator.Validate(new InviteAccountabilityBuddyCommand(
            UserId, Guid.NewGuid(), AccountabilityCadence.Daily, new[] { habitId, habitId }));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Invite_RejectsEmptyGuidHabitId()
    {
        var validator = new InviteAccountabilityBuddyCommandValidator();
        var result = validator.Validate(new InviteAccountabilityBuddyCommand(
            UserId, Guid.NewGuid(), AccountabilityCadence.Daily, new[] { Guid.Empty }));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Accept_RejectsEmptyHabitIds()
    {
        var validator = new AcceptAccountabilityPairCommandValidator();
        var result = validator.Validate(new AcceptAccountabilityPairCommand(UserId, PairId, []));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Accept_AcceptsValidCommand()
    {
        var validator = new AcceptAccountabilityPairCommandValidator();
        var result = validator.Validate(new AcceptAccountabilityPairCommand(UserId, PairId, Habits(2)));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void SetHabits_RejectsEmptyHabitIds()
    {
        var validator = new SetAccountabilityHabitsCommandValidator();
        var result = validator.Validate(new SetAccountabilityHabitsCommand(UserId, PairId, []));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void CheckIn_RejectsNoteOver200Chars()
    {
        var validator = new CheckInAccountabilityCommandValidator();
        var result = validator.Validate(new CheckInAccountabilityCommand(UserId, PairId, new string('x', 201)));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void CheckIn_AcceptsNullNote()
    {
        var validator = new CheckInAccountabilityCommandValidator();
        var result = validator.Validate(new CheckInAccountabilityCommand(UserId, PairId, null));
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void End_RequiresUserId(bool hasUserId)
    {
        var validator = new EndAccountabilityPairCommandValidator();
        var result = validator.Validate(new EndAccountabilityPairCommand(hasUserId ? UserId : Guid.Empty, PairId));
        result.IsValid.Should().Be(hasUserId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetPairs_RequiresUserId(bool hasUserId)
    {
        var validator = new GetAccountabilityPairsQueryValidator();
        var result = validator.Validate(new GetAccountabilityPairsQuery(hasUserId ? UserId : Guid.Empty));
        result.IsValid.Should().Be(hasUserId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetCheckIns_RequiresPairId(bool hasPairId)
    {
        var validator = new GetAccountabilityCheckInsQueryValidator();
        var result = validator.Validate(new GetAccountabilityCheckInsQuery(UserId, hasPairId ? PairId : Guid.Empty));
        result.IsValid.Should().Be(hasPairId);
    }
}
