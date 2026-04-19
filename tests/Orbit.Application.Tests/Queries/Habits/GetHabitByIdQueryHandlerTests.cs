using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;
using System.Reflection;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetHabitByIdQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly GetHabitByIdQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid HabitId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GetHabitByIdQueryHandlerTests()
    {
        _handler = new GetHabitByIdQueryHandler(_habitRepo, _userDateService);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static Habit CreateTestHabit(string title = "Test Habit")
    {
        return Habit.Create(new HabitCreateParams(
            UserId, title, FrequencyUnit.Day, 1,
            DueDate: Today)).Value;
    }

    private static Habit CreateOneTimeHabit(string title, DateOnly dueDate, Guid? parentHabitId = null)
    {
        return Habit.Create(new HabitCreateParams(
            UserId,
            title,
            null,
            null,
            DueDate: dueDate,
            ParentHabitId: parentHabitId)).Value;
    }

    private static void AttachChild(Habit parent, Habit child)
    {
        var field = typeof(Habit).GetField("_children", BindingFlags.Instance | BindingFlags.NonPublic);
        var children = field?.GetValue(parent) as IList<Habit>;
        children.Should().NotBeNull();
        children!.Add(child);
    }

    [Fact]
    public async Task Handle_HabitFound_ReturnsSuccess()
    {
        var habit = CreateTestHabit("My Daily Habit");

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        var query = new GetHabitByIdQuery(UserId, HabitId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("My Daily Habit");
        result.Value.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        result.Value.FrequencyQuantity.Should().Be(1);
        result.Value.DueDate.Should().Be(Today);
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReturnsFailure()
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        var query = new GetHabitByIdQuery(UserId, HabitId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Habit not found");
    }

    [Fact]
    public async Task Handle_WrongUser_ReturnsFailure()
    {
        // The repo query filters by both HabitId and UserId,
        // so a wrong user means the repo returns empty
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        var wrongUserId = Guid.NewGuid();
        var query = new GetHabitByIdQuery(wrongUserId, HabitId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("HABIT_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_HabitFound_MapsAllDetailFields()
    {
        var habit = CreateTestHabit("Detailed Habit");

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        var query = new GetHabitByIdQuery(UserId, HabitId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var detail = result.Value;
        detail.IsCompleted.Should().BeFalse();
        detail.IsBadHabit.Should().BeFalse();
        detail.Children.Should().BeEmpty();
        detail.ChecklistItems.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_OverdueChild_ReturnsChildIsOverdue()
    {
        var parent = CreateTestHabit("Parent Habit");
        var child = CreateOneTimeHabit("Overdue Child", Today.AddDays(-2), parent.Id);
        AttachChild(parent, child);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { parent }.AsReadOnly());

        var query = new GetHabitByIdQuery(UserId, HabitId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Children.Should().ContainSingle();
        result.Value.Children[0].Title.Should().Be("Overdue Child");
        result.Value.Children[0].IsOverdue.Should().BeTrue();
    }
}
