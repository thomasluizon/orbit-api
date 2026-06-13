using FluentAssertions;
using NSubstitute;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Profile;

public class ExportUserDataQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<GoalProgressLog> _goalProgressLogRepo = Substitute.For<IGenericRepository<GoalProgressLog>>();
    private readonly IGenericRepository<Tag> _tagRepo = Substitute.For<IGenericRepository<Tag>>();
    private readonly IGenericRepository<UserFact> _userFactRepo = Substitute.For<IGenericRepository<UserFact>>();
    private readonly ExportUserDataQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public ExportUserDataQueryHandlerTests()
    {
        _handler = new ExportUserDataQueryHandler(
            _userRepo, _habitRepo, _habitLogRepo, _goalRepo, _goalProgressLogRepo, _tagRepo, _userFactRepo);

        ReturnsEmpty(_habitRepo);
        ReturnsEmpty(_habitLogRepo);
        ReturnsEmpty(_goalRepo);
        ReturnsEmpty(_goalProgressLogRepo);
        ReturnsEmpty(_tagRepo);
        ReturnsEmpty(_userFactRepo);
    }

    private static void ReturnsEmpty<T>(IGenericRepository<T> repository) where T : Orbit.Domain.Common.Entity
    {
        repository.FindAsync(
            Arg.Any<Expression<Func<T, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<T>().AsReadOnly());
    }

    private static void Returns<T>(IGenericRepository<T> repository, params T[] entities) where T : Orbit.Domain.Common.Entity
    {
        repository.FindAsync(
            Arg.Any<Expression<Func<T, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(entities.ToList().AsReadOnly());
    }

    private void ArrangeUser(User user)
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
    }

    private static User CreateTestUser() => User.Create("Test User", "test@example.com").Value;

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_EmptyAccount_ReturnsAccountSettingsAndEmptyCollections()
    {
        var user = CreateTestUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        ArrangeUser(user);

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Account.Name.Should().Be("Test User");
        result.Value.Account.Email.Should().Be("test@example.com");
        result.Value.Account.Plan.Should().Be("free");
        result.Value.Settings.AiMemoryEnabled.Should().Be(user.AiMemoryEnabled);
        result.Value.Habits.Should().BeEmpty();
        result.Value.Goals.Should().BeEmpty();
        result.Value.Tags.Should().BeEmpty();
        result.Value.Facts.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ProUser_MapsProPlan()
    {
        var user = CreateTestUser();
        user.StartTrial(DateTime.UtcNow.AddDays(5));
        ArrangeUser(user);

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Account.Plan.Should().Be("pro");
    }

    [Fact]
    public async Task Handle_HabitsWithLogs_GroupsLogsPerHabitOrderedByDate()
    {
        ArrangeUser(CreateTestUser());

        var habitWithLogs = Habit.Create(new HabitCreateParams(
            UserId, "Meditate", FrequencyUnit.Day, 1, DueDate: new DateOnly(2026, 4, 1))).Value;
        var habitWithoutLogs = Habit.Create(new HabitCreateParams(
            UserId, "Stretch", FrequencyUnit.Day, 1, DueDate: new DateOnly(2026, 4, 1))).Value;
        Returns(_habitRepo, habitWithLogs, habitWithoutLogs);

        Returns(
            _habitLogRepo,
            HabitLog.Create(habitWithLogs.Id, new DateOnly(2026, 4, 3), 1),
            HabitLog.Create(habitWithLogs.Id, new DateOnly(2026, 4, 1), 1, "first"),
            HabitLog.Create(habitWithLogs.Id, new DateOnly(2026, 4, 2), 1));

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habits.Should().HaveCount(2);

        var exportedWithLogs = result.Value.Habits.Single(h => h.Id == habitWithLogs.Id);
        exportedWithLogs.Title.Should().Be("Meditate");
        exportedWithLogs.FrequencyUnit.Should().Be("Day");
        exportedWithLogs.Logs.Select(l => l.Date).Should().Equal(
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 2), new DateOnly(2026, 4, 3));
        exportedWithLogs.Logs[0].Note.Should().Be("first");

        result.Value.Habits.Single(h => h.Id == habitWithoutLogs.Id).Logs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_GoalsWithProgress_MapsProgressLogsPerGoal()
    {
        ArrangeUser(CreateTestUser());

        var goalWithProgress = Goal.Create(new Goal.CreateGoalParams(UserId, "Read books", 12, "books")).Value;
        var goalWithoutProgress = Goal.Create(new Goal.CreateGoalParams(UserId, "Save money", 100, "BRL")).Value;
        Returns(_goalRepo, goalWithProgress, goalWithoutProgress);

        Returns(
            _goalProgressLogRepo,
            GoalProgressLog.Create(goalWithProgress.Id, 0, 3, "good month"));

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Goals.Should().HaveCount(2);

        var exportedWithProgress = result.Value.Goals.Single(g => g.Id == goalWithProgress.Id);
        exportedWithProgress.Title.Should().Be("Read books");
        exportedWithProgress.Status.Should().Be("Active");
        exportedWithProgress.ProgressLogs.Should().ContainSingle();
        exportedWithProgress.ProgressLogs[0].Value.Should().Be(3);
        exportedWithProgress.ProgressLogs[0].PreviousValue.Should().Be(0);
        exportedWithProgress.ProgressLogs[0].Note.Should().Be("good month");

        result.Value.Goals.Single(g => g.Id == goalWithoutProgress.Id).ProgressLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_TagsAndFacts_Mapped()
    {
        ArrangeUser(CreateTestUser());

        var tag = Tag.Create(UserId, "Health", "#FF0000").Value;
        Returns(_tagRepo, tag);

        var fact = UserFact.Create(UserId, "Prefers morning workouts", "routine").Value;
        Returns(_userFactRepo, fact);

        var result = await _handler.Handle(new ExportUserDataQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Tags.Should().ContainSingle();
        result.Value.Tags[0].Name.Should().Be("Health");
        result.Value.Tags[0].Color.Should().Be("#FF0000");
        result.Value.Facts.Should().ContainSingle();
        result.Value.Facts[0].FactText.Should().Be("Prefers morning workouts");
        result.Value.Facts[0].Category.Should().Be("routine");
    }
}
