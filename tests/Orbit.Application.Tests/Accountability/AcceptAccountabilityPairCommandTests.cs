using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Accountability.Commands;
using Orbit.Application.Accountability.Services;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Social.Services;
using Orbit.Application.Tests.Social;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Accountability;

public class AcceptAccountabilityPairCommandTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<AccountabilityPair> _pairRepository = Substitute.For<IGenericRepository<AccountabilityPair>>();
    private readonly IGenericRepository<AccountabilityPairHabit> _pairHabitRepository = Substitute.For<IGenericRepository<AccountabilityPairHabit>>();
    private readonly IGenericRepository<AccountabilityCheckIn> _checkInRepository = Substitute.For<IGenericRepository<AccountabilityCheckIn>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepository = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<XpAwardLog> _xpAwardLogRepository = Substitute.For<IGenericRepository<XpAwardLog>>();
    private readonly IGenericRepository<Notification> _notificationRepository = Substitute.For<IGenericRepository<Notification>>();
    private readonly IPushNotificationService _push = Substitute.For<IPushNotificationService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private readonly AcceptAccountabilityPairCommandHandler _handler;

    private readonly User _addressee = SocialTestHelpers.OptedInUser("Addressee");
    private readonly User _requester = SocialTestHelpers.OptedInUser("Requester");
    private readonly Guid _habitId = Guid.NewGuid();
    private readonly AccountabilityPair _pair;

    public AcceptAccountabilityPairCommandTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        var pairService = new AccountabilityPairService(_pairRepository, _pairHabitRepository, _habitRepository);
        var repositories = new AccountabilityRepositories(_userRepository, _pairRepository, _checkInRepository, _achievementRepository);
        var dispatcher = new SocialNotificationDispatcher(
            _notificationRepository, _push, Substitute.For<ILogger<SocialNotificationDispatcher>>());
        _handler = new AcceptAccountabilityPairCommandHandler(
            guard, pairService, repositories, dispatcher, new XpAwarder(_xpAwardLogRepository), _unitOfWork);

        _pair = AccountabilityPair.Create(_requester.Id, _addressee.Id, AccountabilityCadence.Daily).Value;

        SocialTestHelpers.StubUsers(_userRepository, _addressee, _requester);
        AccountabilityTestHelpers.StubFind(_pairRepository, _pair);
        AccountabilityTestHelpers.StubFind(_pairHabitRepository);
        AccountabilityTestHelpers.StubFind(_achievementRepository);
        _habitRepository.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(1);
    }

    private AcceptAccountabilityPairCommand Command(Guid? userId = null, Guid? pairId = null) =>
        new(userId ?? _addressee.Id, pairId ?? _pair.Id, new[] { _habitId });

    [Fact]
    public async Task ByAddressee_AcceptsLinksHabitsAndPushesRequester()
    {
        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _pair.Status.Should().Be(AccountabilityPairStatus.Accepted);
        _pair.AcceptedAtUtc.Should().NotBeNull();
        await _pairHabitRepository.Received(1).AddAsync(
            Arg.Is<AccountabilityPairHabit>(ph => ph.UserId == _addressee.Id && ph.HabitId == _habitId),
            Arg.Any<CancellationToken>());
        await _notificationRepository.Received(1).AddAsync(
            Arg.Is<Notification>(n => n.UserId == _requester.Id && n.Url == "/social?tab=buddies"),
            Arg.Any<CancellationToken>());
        await _push.Received(1).SendToUserAsync(
            _requester.Id, Arg.Any<string>(), Arg.Any<string>(), "/social?tab=buddies", Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownPair_ReturnsPairNotFound()
    {
        AccountabilityTestHelpers.StubFind(_pairRepository);

        var result = await _handler.Handle(Command(pairId: Guid.NewGuid()), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.PairNotFound);
    }

    [Fact]
    public async Task ByNonAddressee_ReturnsPairNotFound()
    {
        var someoneElse = SocialTestHelpers.OptedInUser("Stranger");
        SocialTestHelpers.StubUsers(_userRepository, _addressee, _requester, someoneElse);

        var result = await _handler.Handle(Command(userId: someoneElse.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.PairNotFound);
    }

    [Fact]
    public async Task AlreadyAccepted_ReturnsPairNotPending()
    {
        _pair.Accept();

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAIR_NOT_PENDING");
    }

    [Fact]
    public async Task HabitNotOwnedByAccepter_ReturnsHabitNotFound()
    {
        _habitRepository.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(0);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BattleBuddyAward_GrantsToBothParticipants()
    {
        var requesterXpBefore = _requester.TotalXp;
        var addresseeXpBefore = _addressee.TotalXp;

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _achievementRepository.Received(2).AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == "battle_buddy"), Arg.Any<CancellationToken>());
        _requester.TotalXp.Should().BeGreaterThan(requesterXpBefore);
        _addressee.TotalXp.Should().BeGreaterThan(addresseeXpBefore);
    }
}
