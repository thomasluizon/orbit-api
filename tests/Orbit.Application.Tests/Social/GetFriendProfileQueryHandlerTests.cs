using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Social.Queries;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Social;

public class GetFriendProfileQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Friendship> _friendshipRepository = Substitute.For<IGenericRepository<Friendship>>();
    private readonly IGenericRepository<BlockedUser> _blockedUserRepository = Substitute.For<IGenericRepository<BlockedUser>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepository = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<AccountabilityPair> _accountabilityPairRepository = Substitute.For<IGenericRepository<AccountabilityPair>>();
    private readonly IGenericRepository<Challenge> _challengeRepository = Substitute.For<IGenericRepository<Challenge>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();

    private readonly GetFriendProfileQueryHandler _handler;

    private readonly User _caller = SocialTestHelpers.OptedInUser("Caller");
    private readonly User _friend = SocialTestHelpers.OptedInUser("Friend");

    private static readonly DateOnly Today = new(2026, 6, 15);

    public GetFriendProfileQueryHandlerTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        var friendGraph = new FriendGraphService(_userRepository, _friendshipRepository, _blockedUserRepository);
        _handler = new GetFriendProfileQueryHandler(
            guard,
            friendGraph,
            _userDateService,
            _userRepository,
            _achievementRepository,
            _habitRepository,
            _accountabilityPairRepository,
            _challengeRepository);

        SocialTestHelpers.StubUsers(_userRepository, _caller, _friend);
        SocialTestHelpers.StubFind(_achievementRepository);
        SocialTestHelpers.StubFind(_accountabilityPairRepository);
        SocialTestHelpers.StubFind(_challengeRepository);
        StubHabits();
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today);
    }

    private Friendship AcceptedFriendship()
    {
        var friendship = Friendship.Create(_caller.Id, _friend.Id).Value;
        friendship.Accept();
        return friendship;
    }

    private void StubHabits(params Habit[] habits)
    {
        _habitRepository.FindAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
                Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Habit>)habits);
    }

    private static Habit HabitWithCompletions(Guid userId, string title, int completions)
    {
        var habit = Habit.Create(new HabitCreateParams(userId, title, FrequencyUnit.Day, 1)).Value;
        for (var i = 0; i < completions; i++)
            habit.Log(Today.AddDays(-i), advanceDueDate: false);
        return habit;
    }

    private Challenge SharedChallenge(string title)
    {
        var challenge = Challenge.Create(new CreateChallengeParams(
            _caller.Id, ChallengeType.CoopGoal, title, null, 10,
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30),
            Guid.NewGuid().ToString("N")[..8])).Value;
        challenge.AddParticipant(_caller.Id, []);
        challenge.AddParticipant(_friend.Id, []);
        return challenge;
    }

    [Fact]
    public async Task Handle_AcceptedFriend_ReturnsProfileStatsAndAchievements()
    {
        _friend.AddXp(1_200);
        _friend.SetStreakState(12, 40, new DateOnly(2026, 6, 1));
        SocialTestHelpers.StubFind(_friendshipRepository, AcceptedFriendship());
        SocialTestHelpers.StubFind(_achievementRepository,
            UserAchievement.Create(_friend.Id, AchievementDefinitions.FirstOrbit),
            UserAchievement.Create(_friend.Id, AchievementDefinitions.WeekWarrior));

        var result = await _handler.Handle(new GetFriendProfileQuery(_caller.Id, _friend.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var view = result.Value;
        view.UserId.Should().Be(_friend.Id);
        view.DisplayName.Should().Be("Friend");
        view.Handle.Should().NotBeNullOrWhiteSpace();
        view.CurrentStreak.Should().Be(12);
        view.Level.Should().Be(5);
        view.Achievements.Select(a => a.IconKey).Should().Contain(new[] { "first_orbit", "week_warrior" });
    }

    [Fact]
    public async Task Handle_AcceptedFriend_ReturnsEnrichedStatsActivityHabitsAndSharedContext()
    {
        _friend.AddXp(1_200);
        _friend.SetStreakState(12, 40, new DateOnly(2026, 6, 1));
        var friendship = AcceptedFriendship();
        SocialTestHelpers.StubFind(_friendshipRepository, friendship);

        StubHabits(
            HabitWithCompletions(_friend.Id, "Reading", 4),
            HabitWithCompletions(_friend.Id, "Running", 2));

        var pair = AccountabilityPair.Create(_caller.Id, _friend.Id, AccountabilityCadence.Weekly).Value;
        pair.Accept();
        SocialTestHelpers.StubFind(_accountabilityPairRepository, pair);

        SocialTestHelpers.StubFind(_challengeRepository, SharedChallenge("Sunrise Sprint"));

        var result = await _handler.Handle(new GetFriendProfileQuery(_caller.Id, _friend.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var view = result.Value;
        view.LongestStreak.Should().Be(40);
        view.TotalXp.Should().Be(1_200);
        view.LevelTitle.Should().NotBeNullOrWhiteSpace();
        view.FriendsSinceUtc.Should().Be(friendship.RespondedAtUtc);
        view.WeeklyActivity.Should().HaveCount(7);
        view.WeeklyActivity.Sum().Should().Be(6);
        view.WeeklyActivity[6].Should().Be(2);
        view.TopHabits.Select(h => h.Title).Should().Equal("Reading", "Running");
        view.TopHabits[0].CompletionCount.Should().Be(4);
        view.IsAccountabilityPartner.Should().BeTrue();
        view.SharedChallenges.Should().ContainSingle(c => c.Title == "Sunrise Sprint");
    }

    [Fact]
    public async Task Handle_NoSharedContext_ReturnsEmptyActivityAndFlagsOff()
    {
        SocialTestHelpers.StubFind(_friendshipRepository, AcceptedFriendship());

        var result = await _handler.Handle(new GetFriendProfileQuery(_caller.Id, _friend.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var view = result.Value;
        view.WeeklyActivity.Should().HaveCount(7);
        view.WeeklyActivity.Sum().Should().Be(0);
        view.TopHabits.Should().BeEmpty();
        view.IsAccountabilityPartner.Should().BeFalse();
        view.SharedChallenges.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_NotAcceptedFriends_ReturnsUserNotFound()
    {
        SocialTestHelpers.StubFind(_friendshipRepository);

        var result = await _handler.Handle(new GetFriendProfileQuery(_caller.Id, _friend.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Handle_PendingFriendshipOnly_ReturnsUserNotFound()
    {
        SocialTestHelpers.StubFind(_friendshipRepository, Friendship.Create(_caller.Id, _friend.Id).Value);

        var result = await _handler.Handle(new GetFriendProfileQuery(_caller.Id, _friend.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Handle_CallerOptedOut_ReturnsSocialDisabled()
    {
        var caller = SocialTestHelpers.OptedOutUser("Private");
        SocialTestHelpers.StubUsers(_userRepository, caller, _friend);

        var result = await _handler.Handle(new GetFriendProfileQuery(caller.Id, _friend.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.SocialDisabled);
    }
}
