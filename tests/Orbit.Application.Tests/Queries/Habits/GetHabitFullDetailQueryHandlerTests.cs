using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetHabitFullDetailQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly GetHabitFullDetailQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid HabitId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GetHabitFullDetailQueryHandlerTests()
    {
        _handler = new GetHabitFullDetailQueryHandler(_habitRepo, _habitLogRepo, _userRepo, _userDateService);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static Habit CreateTestHabit()
    {
        return Habit.Create(new HabitCreateParams(
            UserId, "Test Habit", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;
    }

    private static User CreateTestUser()
    {
        return User.Create("Test User", "test@example.com").Value;
    }

    private static void AttachChild(Habit parent, Habit child)
    {
        var field = typeof(Habit).GetField("_children", BindingFlags.Instance | BindingFlags.NonPublic);
        var children = field?.GetValue(parent) as IList<Habit>;
        children.Should().NotBeNull();
        children!.Add(child);
    }

    [Fact]
    public async Task Handle_HabitAndUserFound_ReturnsFullDetail()
    {
        var habit = CreateTestHabit();
        var user = CreateTestUser();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _habitLogRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<HabitLog>().AsReadOnly());

        var query = new GetHabitFullDetailQuery(UserId, HabitId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habit.Should().NotBeNull();
        result.Value.Habit.Title.Should().Be("Test Habit");
        result.Value.Metrics.Should().NotBeNull();
        result.Value.Logs.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReturnsFailure()
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        var query = new GetHabitFullDetailQuery(UserId, HabitId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Habit not found");
        result.ErrorCode.Should().Be("HABIT_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_ForeignUserHabit_DoesNotQueryLogs()
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        var result = await _handler.Handle(new GetHabitFullDetailQuery(Guid.NewGuid(), HabitId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _habitLogRepo.DidNotReceive().FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        var habit = CreateTestHabit();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetHabitFullDetailQuery(UserId, HabitId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_WithLogs_ReturnsLogsInResponse()
    {
        var habit = CreateTestHabit();
        var user = CreateTestUser();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _habitLogRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<HabitLog>().AsReadOnly());

        var query = new GetHabitFullDetailQuery(UserId, HabitId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Logs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_GeneralHabitLoggedToday_ReturnsCompleted()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            "General Habit",
            null,
            null,
            DueDate: Today,
            IsGeneral: true)).Value;
        var log = habit.Log(Today).Value;
        var user = CreateTestUser();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _habitLogRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<HabitLog> { log }.AsReadOnly());

        var result = await _handler.Handle(
            new GetHabitFullDetailQuery(UserId, habit.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habit.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_GeneralDescendantLoggedToday_ReturnsCompleted()
    {
        var parent = CreateTestHabit();
        var child = Habit.Create(new HabitCreateParams(
            UserId,
            "General Child",
            null,
            null,
            DueDate: Today,
            IsGeneral: true,
            ParentHabitId: parent.Id)).Value;
        var log = child.Log(Today).Value;
        var user = CreateTestUser();
        AttachChild(parent, child);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { parent }.AsReadOnly());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _habitLogRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var predicate = callInfo.ArgAt<Expression<Func<HabitLog, bool>>>(0).Compile();
                return new List<HabitLog> { log }.Where(predicate).ToList().AsReadOnly();
            });

        var result = await _handler.Handle(
            new GetHabitFullDetailQuery(UserId, parent.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habit.Children.Should().ContainSingle();
        result.Value.Habit.Children[0].IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_CompletedOneTimeHabitWithoutLogToday_ReturnsCompleted()
    {
        var dueDate = Today.AddDays(-1);
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            "Completed Task",
            null,
            null,
            DueDate: dueDate)).Value;
        var log = habit.Log(dueDate).Value;
        var user = CreateTestUser();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _habitLogRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<HabitLog> { log }.AsReadOnly());

        var result = await _handler.Handle(
            new GetHabitFullDetailQuery(UserId, habit.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Habit.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_NestedHabitWithRelationships_ReturnsLinkedGoalsAndSlipAlertEnabled()
    {
        var parentId = Guid.NewGuid();
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            "Nested Habit",
            FrequencyUnit.Day,
            1,
            DueDate: Today,
            ParentHabitId: parentId,
            SlipAlertEnabled: true)).Value;
        var firstGoal = Goal.Create(UserId, "First Goal", 10, "times").Value;
        var secondGoal = Goal.Create(UserId, "Second Goal", 20, "times").Value;
        habit.AddGoal(firstGoal);
        habit.AddGoal(secondGoal);
        var user = CreateTestUser();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _habitLogRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<HabitLog>().AsReadOnly());

        var result = await _handler.Handle(
            new GetHabitFullDetailQuery(UserId, habit.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            result.Value.Habit,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        json.RootElement.TryGetProperty("linkedGoals", out var linkedGoals).Should().BeTrue();
        linkedGoals.EnumerateArray()
            .Select(goal => (goal.GetProperty("id").GetGuid(), goal.GetProperty("title").GetString()))
            .Should().BeEquivalentTo(new[]
            {
                (firstGoal.Id, firstGoal.Title),
                (secondGoal.Id, secondGoal.Title)
            });
        json.RootElement.GetProperty("slipAlertEnabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Handle_NestedHabitWithoutGoals_ReturnsEmptyLinkedGoals()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            "Nested Habit",
            FrequencyUnit.Day,
            1,
            DueDate: Today,
            ParentHabitId: Guid.NewGuid())).Value;
        var user = CreateTestUser();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _habitLogRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<HabitLog>().AsReadOnly());

        var result = await _handler.Handle(
            new GetHabitFullDetailQuery(UserId, habit.Id),
            CancellationToken.None);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            result.Value.Habit,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        json.RootElement.GetProperty("linkedGoals").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Handle_TopLevelHabit_SerializesExistingContractOnly()
    {
        var habit = CreateTestHabit();
        var user = CreateTestUser();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _habitLogRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<HabitLog>().AsReadOnly());

        var result = await _handler.Handle(
            new GetHabitFullDetailQuery(UserId, habit.Id),
            CancellationToken.None);

        var json = JsonSerializer.Serialize(
            result.Value.Habit,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().NotContain("linkedGoals");
        json.Should().NotContain("slipAlertEnabled");
    }
}
