using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetHabitWidgetQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly GetHabitWidgetQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GetHabitWidgetQueryHandlerTests()
    {
        _handler = new GetHabitWidgetQueryHandler(_habitRepository, _userRepository, _userDateService, _unitOfWork);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _userRepository.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(User.Create("Orbit User", "orbit@example.com").Value);
    }

    [Fact]
    public async Task Handle_ReturnsCompactTodayItems()
    {
        var habit = CreateHabit("Morning run", Today);
        SetupHabits(habit);

        var result = await _handler.Handle(new GetHabitWidgetQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DayOffset.Should().Be(0);
        result.Value.Items.Should().ContainSingle();
        result.Value.Items[0].Title.Should().Be("Morning run");
    }

    [Fact]
    public async Task Handle_UsesTomorrowWhenTodayIsComplete()
    {
        var todayHabit = CreateHabit("Done today", Today);
        todayHabit.Log(Today);
        var tomorrowHabit = CreateHabit("Tomorrow run", Today.AddDays(1));
        SetupHabits(todayHabit, tomorrowHabit);

        var result = await _handler.Handle(new GetHabitWidgetQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.DayOffset.Should().Be(1);
        result.Value.Items.Should().ContainSingle(item => item.Title == "Tomorrow run");
    }

    private static Habit CreateHabit(string title, DateOnly dueDate)
    {
        return Habit.Create(new HabitCreateParams(
            UserId,
            title,
            FrequencyUnit.Day,
            1,
            DueDate: dueDate)).Value;
    }

    private void SetupHabits(params Habit[] habits)
    {
        _habitRepository.FindAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(habits.ToList().AsReadOnly());
    }
}
