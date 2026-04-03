using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetHabitScheduleQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly GetHabitScheduleQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GetHabitScheduleQueryHandlerTests()
    {
        _handler = new GetHabitScheduleQueryHandler(_habitRepo, _userDateService, _unitOfWork);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static Habit CreateTestHabit(
        string title = "Test Habit",
        FrequencyUnit? frequencyUnit = FrequencyUnit.Day,
        int? frequencyQuantity = 1,
        DateOnly? dueDate = null,
        bool isCompleted = false,
        bool isGeneral = false,
        string? description = null)
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, title, frequencyUnit, frequencyQuantity,
            Description: description,
            DueDate: dueDate ?? Today,
            IsGeneral: isGeneral)).Value;

        if (isCompleted && frequencyUnit is null)
            habit.Log(dueDate ?? Today);

        return habit;
    }

    private void SetupHabits(params Habit[] habits)
    {
        var habitList = habits.ToList().AsReadOnly();
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habitList);
    }

    [Fact]
    public async Task Handle_NoHabits_ReturnsEmptyPaginatedResponse()
    {
        SetupHabits();
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6));
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_SingleDailyHabitInRange_ReturnsHabit()
    {
        var habit = CreateTestHabit(dueDate: Today);
        SetupHabits(habit);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6));
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Test Habit");
    }

    [Fact]
    public async Task Handle_OneTimeTaskOverdue_WhenIncludeOverdue_ReturnsAsOverdue()
    {
        var pastDate = Today.AddDays(-5);
        var habit = CreateTestHabit(title: "Overdue Task", frequencyUnit: null, frequencyQuantity: null, dueDate: pastDate);
        SetupHabits(habit);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), IncludeOverdue: true);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].IsOverdue.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_OneTimeTaskOverdue_WhenNotIncludeOverdue_ExcludesIt()
    {
        var pastDate = Today.AddDays(-5);
        var habit = CreateTestHabit(title: "Overdue", frequencyUnit: null, frequencyQuantity: null, dueDate: pastDate);
        SetupHabits(habit);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), IncludeOverdue: false);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_Pagination_ReturnsCorrectPage()
    {
        var habits = Enumerable.Range(1, 5).Select(i => CreateTestHabit(title: "Habit" + i, dueDate: Today)).ToArray();
        SetupHabits(habits);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), PageSize: 2, Page: 2);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Page.Should().Be(2);
        result.Value.TotalCount.Should().Be(5);
        result.Value.TotalPages.Should().Be(3);
        result.Value.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_SearchFilter_FiltersHabitsByTitle()
    {
        var matching = CreateTestHabit(title: "Morning Workout", dueDate: Today);
        var other = CreateTestHabit(title: "Read Book", dueDate: Today);
        SetupHabits(matching, other);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), Search: "Workout");
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Morning Workout");
    }

    [Fact]
    public async Task Handle_FrequencyFilter_FiltersOneTimeTasks()
    {
        var daily = CreateTestHabit(title: "Daily", frequencyUnit: FrequencyUnit.Day, dueDate: Today);
        var oneTime = CreateTestHabit(title: "One-time", frequencyUnit: null, frequencyQuantity: null, dueDate: Today);
        SetupHabits(daily, oneTime);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), FrequencyUnitFilter: "none");
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("One-time");
    }

    [Fact]
    public async Task Handle_FrequencyFilter_FiltersByUnit()
    {
        var daily = CreateTestHabit(title: "Daily", frequencyUnit: FrequencyUnit.Day, dueDate: Today);
        var weekly = CreateTestHabit(title: "Weekly", frequencyUnit: FrequencyUnit.Week, dueDate: Today);
        SetupHabits(daily, weekly);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), FrequencyUnitFilter: "Week");
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Title.Should().Be("Weekly");
    }

    [Fact]
    public async Task Handle_NoDateRange_ReturnsAllHabits()
    {
        var habit = CreateTestHabit(title: "Some Habit", dueDate: Today);
        SetupHabits(habit);
        var query = new GetHabitScheduleQuery(UserId);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task Handle_GeneralHabits_ReturnsGeneralOnly()
    {
        var general = CreateTestHabit(title: "General", frequencyUnit: null, frequencyQuantity: null, isGeneral: true);
        SetupHabits(general);
        var query = new GetHabitScheduleQuery(UserId, IsGeneral: true);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].IsGeneral.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_PageBeyondTotal_ClampsToLastPage()
    {
        SetupHabits(CreateTestHabit(dueDate: Today));
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), PageSize: 10, Page: 999);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Page.Should().Be(1);
    }

    [Fact]
    public async Task Handle_IsCompletedFilter_FiltersCompletedHabits()
    {
        var active = CreateTestHabit(title: "Active", dueDate: Today);
        var done = CreateTestHabit(title: "Done", frequencyUnit: null, frequencyQuantity: null, dueDate: Today, isCompleted: true);
        SetupHabits(active, done);
        var query = new GetHabitScheduleQuery(UserId, Today, Today.AddDays(6), IsCompleted: false);
        var result = await _handler.Handle(query, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().OnlyContain(h => !h.IsCompleted);
    }
}
