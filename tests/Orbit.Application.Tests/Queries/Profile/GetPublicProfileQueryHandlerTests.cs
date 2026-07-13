using FluentAssertions;
using NSubstitute;
using Orbit.Application.Gamification;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;
using System.Text.Json;

namespace Orbit.Application.Tests.Queries.Profile;

public class GetPublicProfileQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepo = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly GetPublicProfileQueryHandler _handler;

    private const string Slug = "ABCDEFGHJKLMNPQRSTUV12";

    private static readonly string[] ExpectedAchievementKeys = new[] { "first_orbit", "week_warrior" };

    public GetPublicProfileQueryHandlerTests()
    {
        _handler = new GetPublicProfileQueryHandler(_userRepo, _achievementRepo, _habitRepo);
    }

    private static User BuildOwner(Action<User>? configure = null)
    {
        var user = User.Create("Ana Clara", "ana@example.com").Value;
        user.AddXp(1_200);
        user.SetStreakState(7, 30, new DateOnly(2026, 6, 1));
        user.SetLanguage("pt-BR");
        user.SetHandle("ana_clara");
        user.SetPublicProfileSlug(Slug);
        configure?.Invoke(user);
        return user;
    }

    private void StubUserBySlug(User? user)
    {
        _userRepo.FindAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(user is null
                ? (IReadOnlyList<User>)new List<User>()
                : new List<User> { user });
    }

    private void StubAchievements(params string[] ids)
    {
        _achievementRepo.FindAsync(Arg.Any<Expression<Func<UserAchievement, bool>>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<UserAchievement>)ids.Select(id => UserAchievement.Create(Guid.NewGuid(), id)).ToList());
    }

    [Fact]
    public async Task Handle_UnknownSlug_ReturnsFailure()
    {
        StubUserBySlug(null);

        var result = await _handler.Handle(new GetPublicProfileQuery("nope"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_EmptySlug_ReturnsFailureWithoutQuerying()
    {
        var result = await _handler.Handle(new GetPublicProfileQuery("   "), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _userRepo.DidNotReceive().FindAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AllStatFlagsOn_ReturnsPublicFieldsAndOwnerLanguage()
    {
        var user = BuildOwner(u => u.SetPublicProfileVisibility(true, true, true, false));
        StubUserBySlug(user);
        StubAchievements(AchievementDefinitions.FirstOrbit, AchievementDefinitions.WeekWarrior);

        var result = await _handler.Handle(new GetPublicProfileQuery(Slug), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var view = result.Value;
        view.DisplayName.Should().Be("Ana Clara");
        view.Handle.Should().Be("ana_clara");
        view.Language.Should().Be("pt-BR");
        view.CurrentStreak.Should().Be(7);
        view.LongestStreak.Should().Be(30);
        view.Level.Should().NotBeNull();
        view.LevelTitle.Should().NotBeNullOrWhiteSpace();
        view.Achievements.Should().NotBeNull();
        view.Achievements!.Select(a => a.IconKey).Should().Contain(ExpectedAchievementKeys);
        view.TopHabits.Should().BeNull();
    }

    [Fact]
    public async Task Handle_StreakFlagOff_OmitsStreak()
    {
        var user = BuildOwner(u => u.SetPublicProfileVisibility(false, true, true, false));
        StubUserBySlug(user);
        StubAchievements();

        var result = await _handler.Handle(new GetPublicProfileQuery(Slug), CancellationToken.None);

        result.Value.CurrentStreak.Should().BeNull();
        result.Value.LongestStreak.Should().BeNull();
    }

    [Fact]
    public async Task Handle_LevelFlagOff_OmitsLevel()
    {
        var user = BuildOwner(u => u.SetPublicProfileVisibility(true, false, true, false));
        StubUserBySlug(user);
        StubAchievements();

        var result = await _handler.Handle(new GetPublicProfileQuery(Slug), CancellationToken.None);

        result.Value.Level.Should().BeNull();
        result.Value.LevelTitle.Should().BeNull();
    }

    [Fact]
    public async Task Handle_AchievementsFlagOff_OmitsAchievementsWithoutQuerying()
    {
        var user = BuildOwner(u => u.SetPublicProfileVisibility(true, true, false, false));
        StubUserBySlug(user);

        var result = await _handler.Handle(new GetPublicProfileQuery(Slug), CancellationToken.None);

        result.Value.Achievements.Should().BeNull();
        await _achievementRepo.DidNotReceive().FindAsync(Arg.Any<Expression<Func<UserAchievement, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TopHabitsFlagOn_ReturnsTopThreeByCompletionsWithNameTieBreak()
    {
        var user = BuildOwner(u => u.SetPublicProfileVisibility(false, false, false, true));
        StubUserBySlug(user);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habits = new List<Habit>
        {
            HabitWithCompletions(user.Id, "Reading", today, 4),
            HabitWithCompletions(user.Id, "Apples", today, 2),
            HabitWithCompletions(user.Id, "Bananas", today, 2),
            HabitWithCompletions(user.Id, "Cycling", today, 1),
            HabitWithCompletions(user.Id, "Zucchini", today, 0)
        };
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Habit>)habits);

        var result = await _handler.Handle(new GetPublicProfileQuery(Slug), CancellationToken.None);

        result.Value.TopHabits.Should().Equal("Reading", "Apples", "Bananas");
    }

    [Fact]
    public async Task Handle_SerializedView_ContainsNoPii()
    {
        var user = BuildOwner(u => u.SetPublicProfileVisibility(true, true, true, false));
        StubUserBySlug(user);
        StubAchievements(AchievementDefinitions.FirstOrbit);

        var result = await _handler.Handle(new GetPublicProfileQuery(Slug), CancellationToken.None);
        var json = JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().NotContain("ana@example.com");
        json.Should().NotContain("email");
        json.Should().NotContain("userId");
        json.Should().NotContain(user.Id.ToString());
    }

    private static Habit HabitWithCompletions(Guid userId, string title, DateOnly today, int completions)
    {
        var habit = Habit.Create(new HabitCreateParams(userId, title, FrequencyUnit.Day, 1, DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;
        for (var i = 0; i < completions; i++)
            habit.Log(today.AddDays(-i), advanceDueDate: false);
        return habit;
    }
}
