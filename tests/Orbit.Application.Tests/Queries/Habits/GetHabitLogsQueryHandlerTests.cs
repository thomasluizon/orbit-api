using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetHabitLogsQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IHabitLogReader _habitLogReader = Substitute.For<IHabitLogReader>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly GetHabitLogsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid HabitId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GetHabitLogsQueryHandlerTests()
    {
        _handler = new GetHabitLogsQueryHandler(_habitRepo, _habitLogReader, _userDateService);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static Habit CreateTestHabit()
    {
        return Habit.Create(new HabitCreateParams(
            UserId, "Test", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;
    }

    private void SetupHabitFound(Habit? habit)
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);
    }

    private void SetupLogs(params HabitLog[] logs)
    {
        _habitLogReader.ReadRecentLogsAsync(
            Arg.Any<Guid>(),
            Arg.Any<DateOnly>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>())
            .Returns(logs);
    }

    [Fact]
    public async Task Handle_HabitFound_ReturnsLogs()
    {
        SetupHabitFound(CreateTestHabit());
        SetupLogs();

        var result = await _handler.Handle(new GetHabitLogsQuery(UserId, HabitId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReturnsFailure()
    {
        SetupHabitFound(null);

        var result = await _handler.Handle(new GetHabitLogsQuery(UserId, HabitId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Habit not found");
        result.ErrorCode.Should().Be("HABIT_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_HabitNotFound_DoesNotQueryLogs()
    {
        SetupHabitFound(null);

        await _handler.Handle(new GetHabitLogsQuery(UserId, HabitId), CancellationToken.None);

        await _habitLogReader.DidNotReceive().ReadRecentLogsAsync(
            Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WrongUser_ReturnsHabitNotFound()
    {
        SetupHabitFound(null);

        var result = await _handler.Handle(new GetHabitLogsQuery(Guid.NewGuid(), HabitId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_ReadsLogsWithDefaultLookbackAndRowCap()
    {
        SetupHabitFound(CreateTestHabit());
        SetupLogs();

        await _handler.Handle(new GetHabitLogsQuery(UserId, HabitId), CancellationToken.None);

        await _habitLogReader.Received(1).ReadRecentLogsAsync(
            HabitId,
            Today.AddDays(-AppConstants.HabitLogsLookbackDays),
            AppConstants.MaxHabitLogsReturned,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_MapsReaderRowsPreservingOrder()
    {
        var habit = CreateTestHabit();
        var newer = habit.Log(new DateOnly(2026, 4, 2)).Value;
        var older = habit.Log(new DateOnly(2026, 3, 30)).Value;
        SetupHabitFound(habit);
        SetupLogs(newer, older);

        var result = await _handler.Handle(new GetHabitLogsQuery(UserId, HabitId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(l => l.Id).Should().ContainInOrder(newer.Id, older.Id);
        result.Value[0].Date.Should().Be(new DateOnly(2026, 4, 2));
        result.Value[0].Value.Should().Be(newer.Value);
    }

    [Fact]
    public async Task Handle_CallsUserDateService()
    {
        SetupHabitFound(CreateTestHabit());
        SetupLogs();

        await _handler.Handle(new GetHabitLogsQuery(UserId, HabitId), CancellationToken.None);

        await _userDateService.Received(1).GetUserTodayAsync(UserId, Arg.Any<CancellationToken>());
    }
}
