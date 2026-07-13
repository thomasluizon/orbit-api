using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Social.Commands;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Social;

public class SendCheerCommandTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Friendship> _friendshipRepository = Substitute.For<IGenericRepository<Friendship>>();
    private readonly IGenericRepository<BlockedUser> _blockedUserRepository = Substitute.For<IGenericRepository<BlockedUser>>();
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Cheer> _cheerRepository = Substitute.For<IGenericRepository<Cheer>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepository = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly IGenericRepository<Notification> _notificationRepository = Substitute.For<IGenericRepository<Notification>>();
    private readonly IGenericRepository<XpAwardLog> _xpAwardLogRepository = Substitute.For<IGenericRepository<XpAwardLog>>();
    private readonly IContentModerationService _moderation = Substitute.For<IContentModerationService>();
    private readonly IPushNotificationService _push = Substitute.For<IPushNotificationService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private readonly SendCheerCommandHandler _handler;

    private readonly User _sender = SocialTestHelpers.OptedInUser("Sender");
    private readonly User _recipient = SocialTestHelpers.OptedInUser("Recipient");
    private readonly Guid _habitId = Guid.NewGuid();

    public SendCheerCommandTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        var friendGraph = new FriendGraphService(_userRepository, _friendshipRepository, _blockedUserRepository);
        var repos = new SendCheerRepositories(_userRepository, _habitRepository, _cheerRepository, _achievementRepository);
        var dispatcher = new SocialNotificationDispatcher(
            _notificationRepository, _push, Substitute.For<ILogger<SocialNotificationDispatcher>>());
        _handler = new SendCheerCommandHandler(
            new SocialInteractionServices(guard, friendGraph, dispatcher), repos, _moderation,
            new XpAwarder(_xpAwardLogRepository), _unitOfWork,
            Substitute.For<ILogger<SendCheerCommandHandler>>());

        SocialTestHelpers.StubUsers(_userRepository, _sender, _recipient);
        SocialTestHelpers.StubFind(_friendshipRepository, AcceptedFriendship());
        SocialTestHelpers.StubFind(_blockedUserRepository);
        SocialTestHelpers.StubFind(_achievementRepository);
        _habitRepository.AnyAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(true);
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(false, false, []));
    }

    private Friendship AcceptedFriendship()
    {
        var friendship = Friendship.Create(_sender.Id, _recipient.Id).Value;
        friendship.Accept();
        return friendship;
    }

    private SendCheerCommand Command(string? note = "Keep it up!") =>
        new(_sender.Id, _recipient.Id, _habitId, note);

    [Fact]
    public async Task CleanNote_PersistsCheerAndPushesRecipient()
    {
        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _cheerRepository.Received(1).AddAsync(
            Arg.Is<Cheer>(c => c.SenderId == _sender.Id && c.RecipientId == _recipient.Id && c.HabitId == _habitId),
            Arg.Any<CancellationToken>());
        await _notificationRepository.Received(1).AddAsync(
            Arg.Is<Notification>(n => n.UserId == _recipient.Id && n.Url == "/social?tab=feed"),
            Arg.Any<CancellationToken>());
        await _push.Received(1).SendToUserAsync(
            _recipient.Id, Arg.Any<string>(), Arg.Any<string>(), "/social?tab=feed", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlaggedNote_RejectsAndPersistsNothingAndDoesNotPush()
    {
        _moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(Flagged: true, Unavailable: false, ["harassment"]));

        var result = await _handler.Handle(Command("nasty text"), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.ContentRejected);
        await _cheerRepository.DidNotReceive().AddAsync(Arg.Any<Cheer>(), Arg.Any<CancellationToken>());
        await _notificationRepository.DidNotReceive().AddAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>());
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
        await _cheerRepository.Received(1).AddAsync(Arg.Any<Cheer>(), Arg.Any<CancellationToken>());
        await _push.Received(1).SendToUserAsync(
            _recipient.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptyNote_SkipsModeration()
    {
        var result = await _handler.Handle(Command(null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _moderation.DidNotReceive().CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonFriendRecipient_ReturnsNotFriends()
    {
        SocialTestHelpers.StubFind(_friendshipRepository);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.NotFriends);
        await _cheerRepository.DidNotReceive().AddAsync(Arg.Any<Cheer>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlockedRecipient_ReturnsBlocked()
    {
        SocialTestHelpers.StubFind(_blockedUserRepository, BlockedUser.Create(_recipient.Id, _sender.Id).Value);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.Blocked);
    }

    [Fact]
    public async Task OptedOutRecipient_ReturnsNotFriends()
    {
        var privateRecipient = SocialTestHelpers.OptedOutUser("Private");
        SocialTestHelpers.StubUsers(_userRepository, _sender, privateRecipient);

        var result = await _handler.Handle(
            new SendCheerCommand(_sender.Id, privateRecipient.Id, _habitId, "hi"), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.NotFriends);
    }

    [Fact]
    public async Task HabitNotOwnedByRecipient_ReturnsHabitNotFound()
    {
        _habitRepository.AnyAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
    }

    [Fact]
    public async Task FirstCheer_AwardsAchievementAndXp()
    {
        var xpBefore = _sender.TotalXp;

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _achievementRepository.Received(1).AddAsync(
            Arg.Is<UserAchievement>(a => a.UserId == _sender.Id && a.AchievementId == AchievementDefinitions.FirstCheer),
            Arg.Any<CancellationToken>());
        _sender.TotalXp.Should().BeGreaterThan(xpBefore);
    }

    [Fact]
    public async Task FirstCheer_NotReAwardedWhenAlreadyEarned()
    {
        SocialTestHelpers.StubFind(_achievementRepository, UserAchievement.Create(_sender.Id, AchievementDefinitions.FirstCheer));

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _achievementRepository.DidNotReceive().AddAsync(Arg.Any<UserAchievement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NullHabitId_PersistsGeneralCheerAndSkipsHabitOwnershipCheck()
    {
        _habitRepository.AnyAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _handler.Handle(
            new SendCheerCommand(_sender.Id, _recipient.Id, null, "You've got this!"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _cheerRepository.Received(1).AddAsync(
            Arg.Is<Cheer>(c => c.SenderId == _sender.Id && c.RecipientId == _recipient.Id && c.HabitId == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TenthSentCheer_PersistsCheerAndEvaluatesCheerleaderThreshold()
    {
        _cheerRepository.CountAsync(Arg.Any<Expression<Func<Cheer, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(CheerleaderThreshold - 1);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _cheerRepository.Received(1).AddAsync(Arg.Any<Cheer>(), Arg.Any<CancellationToken>());
        await _cheerRepository.Received().CountAsync(Arg.Any<Expression<Func<Cheer, bool>>>(), Arg.Any<CancellationToken>());
    }

    private const int CheerleaderThreshold = 10;
}
