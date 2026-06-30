using FluentAssertions;
using NSubstitute;
using Orbit.Application.Accountability.Queries;
using Orbit.Application.Social.Services;
using Orbit.Application.Tests.Social;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Accountability;

public class GetAccountabilityPairsQueryTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<AccountabilityPair> _pairRepository = Substitute.For<IGenericRepository<AccountabilityPair>>();
    private readonly IGenericRepository<AccountabilityPairHabit> _pairHabitRepository = Substitute.For<IGenericRepository<AccountabilityPairHabit>>();
    private readonly IGenericRepository<AccountabilityCheckIn> _checkInRepository = Substitute.For<IGenericRepository<AccountabilityCheckIn>>();

    private readonly GetAccountabilityPairsQueryHandler _handler;

    private readonly User _caller = SocialTestHelpers.OptedInUser("Caller");
    private readonly User _buddyActive = SocialTestHelpers.OptedInUser("ActiveBuddy");
    private readonly User _buddyIncoming = SocialTestHelpers.OptedInUser("IncomingBuddy");
    private readonly User _buddyOutgoing = SocialTestHelpers.OptedInUser("OutgoingBuddy");

    public GetAccountabilityPairsQueryTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        _handler = new GetAccountabilityPairsQueryHandler(
            guard, _pairRepository, _pairHabitRepository, _checkInRepository, _userRepository);
    }

    [Fact]
    public async Task PartitionsActiveIncomingOutgoing_WithHabitsAndLastCheckIns()
    {
        var activePair = AccountabilityPair.Create(_caller.Id, _buddyActive.Id, AccountabilityCadence.Weekly).Value;
        activePair.Accept();
        var incomingPair = AccountabilityPair.Create(_buddyIncoming.Id, _caller.Id, AccountabilityCadence.Daily).Value;
        var outgoingPair = AccountabilityPair.Create(_caller.Id, _buddyOutgoing.Id, AccountabilityCadence.Daily).Value;

        var myHabitId = Guid.NewGuid();
        var buddyHabitId = Guid.NewGuid();
        var myLastDate = new DateOnly(2026, 6, 29);
        var buddyLastDate = new DateOnly(2026, 6, 30);

        SocialTestHelpers.StubUsers(_userRepository, _caller, _buddyActive, _buddyIncoming, _buddyOutgoing);
        AccountabilityTestHelpers.StubFind(_pairRepository, activePair, incomingPair, outgoingPair);
        AccountabilityTestHelpers.StubFind(_pairHabitRepository,
            AccountabilityPairHabit.Create(activePair.Id, _caller.Id, myHabitId),
            AccountabilityPairHabit.Create(activePair.Id, _buddyActive.Id, buddyHabitId));
        AccountabilityTestHelpers.StubFind(_checkInRepository,
            AccountabilityCheckIn.Create(activePair.Id, _caller.Id, myLastDate, null).Value,
            AccountabilityCheckIn.Create(activePair.Id, _buddyActive.Id, buddyLastDate, null).Value);

        var result = await _handler.Handle(new GetAccountabilityPairsQuery(_caller.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var active = result.Value.ActivePairs.Should().ContainSingle().Subject;
        active.Buddy.UserId.Should().Be(_buddyActive.Id);
        active.Status.Should().Be(AccountabilityPairStatus.Accepted);
        active.Cadence.Should().Be(AccountabilityCadence.Weekly);
        active.IsInitiatedByMe.Should().BeTrue();
        active.MyHabitIds.Should().ContainSingle().Which.Should().Be(myHabitId);
        active.BuddyHabitIds.Should().ContainSingle().Which.Should().Be(buddyHabitId);
        active.MyLastCheckInDate.Should().Be(myLastDate);
        active.BuddyLastCheckInDate.Should().Be(buddyLastDate);

        result.Value.IncomingInvites.Should().ContainSingle(p => p.Buddy.UserId == _buddyIncoming.Id && !p.IsInitiatedByMe);
        result.Value.OutgoingInvites.Should().ContainSingle(p => p.Buddy.UserId == _buddyOutgoing.Id && p.IsInitiatedByMe);
    }

    [Fact]
    public async Task NoPairs_ReturnsEmptyLists()
    {
        SocialTestHelpers.StubUsers(_userRepository, _caller);
        AccountabilityTestHelpers.StubFind(_pairRepository);

        var result = await _handler.Handle(new GetAccountabilityPairsQuery(_caller.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ActivePairs.Should().BeEmpty();
        result.Value.IncomingInvites.Should().BeEmpty();
        result.Value.OutgoingInvites.Should().BeEmpty();
    }
}
