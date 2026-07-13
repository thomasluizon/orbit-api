using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Accountability.Commands;
using Orbit.Application.Accountability.Services;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Application.Tests.Social;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Accountability;

public class CheckInAccountabilityCommandTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<BlockedUser> _blockedUserRepository = Substitute.For<IGenericRepository<BlockedUser>>();
    private readonly IGenericRepository<AccountabilityPair> _pairRepository = Substitute.For<IGenericRepository<AccountabilityPair>>();
    private readonly IGenericRepository<AccountabilityPairHabit> _pairHabitRepository = Substitute.For<IGenericRepository<AccountabilityPairHabit>>();
    private readonly IGenericRepository<AccountabilityCheckIn> _checkInRepository = Substitute.For<IGenericRepository<AccountabilityCheckIn>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepository = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Notification> _notificationRepository = Substitute.For<IGenericRepository<Notification>>();
    private readonly IContentModerationService _moderation = Substitute.For<IContentModerationService>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IPushNotificationService _push = Substitute.For<IPushNotificationService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private readonly CheckInAccountabilityCommandHandler _handler;

    private readonly User _caller = SocialTestHelpers.OptedInUser("Caller");
    private readonly User _buddy = SocialTestHelpers.OptedInUser("Buddy");
    private readonly AccountabilityPair _pair;

    public CheckInAccountabilityCommandTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        var friendGraph = new FriendGraphService(_userRepository, Substitute.For<IGenericRepository<Friendship>>(), _blockedUserRepository);
        var pairService = new AccountabilityPairService(_pairRepository, _pairHabitRepository, _habitRepository);
        var repositories = new AccountabilityRepositories(_userRepository, _pairRepository, _checkInRepository, _achievementRepository);
        var dispatcher = new SocialNotificationDispatcher(
            _notificationRepository, _push, Substitute.For<ILogger<SocialNotificationDispatcher>>());
        _handler = new CheckInAccountabilityCommandHandler(
            new SocialInteractionServices(guard, friendGraph, dispatcher), pairService, repositories,
            _moderation, _userDateService, _unitOfWork,
            Substitute.For<ILogger<CheckInAccountabilityCommandHandler>>());

        _pair = AccountabilityPair.Create(_caller.Id, _buddy.Id, AccountabilityCadence.Daily).Value;
        _pair.Accept();

        SocialTestHelpers.StubUsers(_userRepository, _caller, _buddy);
        AccountabilityTestHelpers.StubFind(_pairRepository, _pair);
        AccountabilityTestHelpers.StubFind(_blockedUserRepository);
        AccountabilityTestHelpers.StubFind(_checkInRepository);
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 6, 30));
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(false, false, []));
    }

    private CheckInAccountabilityCommand Command(string? note = "Done today") =>
        new(_caller.Id, _pair.Id, note);

    [Fact]
    public async Task CleanNote_PersistsCheckInAndPushesBuddy()
    {
        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _checkInRepository.Received(1).AddAsync(
            Arg.Is<AccountabilityCheckIn>(c =>
                c.PairId == _pair.Id && c.UserId == _caller.Id && c.Date == new DateOnly(2026, 6, 30)),
            Arg.Any<CancellationToken>());
        await _notificationRepository.Received(1).AddAsync(
            Arg.Is<Notification>(n => n.UserId == _buddy.Id && n.Url == "/social?tab=buddies"),
            Arg.Any<CancellationToken>());
        await _push.Received(1).SendToUserAsync(
            _buddy.Id, Arg.Any<string>(), Arg.Any<string>(), "/social?tab=buddies", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlaggedNote_RejectsAndPersistsNothingAndDoesNotPush()
    {
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(Flagged: true, Unavailable: false, ["harassment"]));

        var result = await _handler.Handle(Command("nasty text"), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.ContentRejected);
        await _checkInRepository.DidNotReceive().AddAsync(Arg.Any<AccountabilityCheckIn>(), Arg.Any<CancellationToken>());
        await _push.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ModerationUnavailable_FailsOpenAndPersists()
    {
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(Flagged: false, Unavailable: true, []));

        var result = await _handler.Handle(Command("maybe risky"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _checkInRepository.Received(1).AddAsync(Arg.Any<AccountabilityCheckIn>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptyNote_SkipsModeration()
    {
        var result = await _handler.Handle(Command(null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _moderation.DidNotReceive().CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotParticipant_ReturnsPairNotFound()
    {
        AccountabilityTestHelpers.StubFind(_pairRepository);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.PairNotFound);
    }

    [Fact]
    public async Task PairNotAccepted_ReturnsPairNotFound()
    {
        var pendingPair = AccountabilityPair.Create(_caller.Id, _buddy.Id, AccountabilityCadence.Daily).Value;
        AccountabilityTestHelpers.StubFind(_pairRepository, pendingPair);

        var result = await _handler.Handle(
            new CheckInAccountabilityCommand(_caller.Id, pendingPair.Id, "hi"), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.PairNotFound);
    }

    [Fact]
    public async Task Blocked_ReturnsBlocked()
    {
        AccountabilityTestHelpers.StubFind(_blockedUserRepository, BlockedUser.Create(_buddy.Id, _caller.Id).Value);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.Blocked);
    }

    [Fact]
    public async Task AlreadyCheckedInToday_ReturnsAlreadyCheckedIn()
    {
        _checkInRepository.AnyAsync(Arg.Any<Expression<Func<AccountabilityCheckIn, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.AlreadyCheckedIn);
        await _checkInRepository.DidNotReceive().AddAsync(Arg.Any<AccountabilityCheckIn>(), Arg.Any<CancellationToken>());
    }
}
