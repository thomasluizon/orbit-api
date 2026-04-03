using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Habits;

public class SkipHabitCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly SkipHabitCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20); // Friday

    public SkipHabitCommandHandlerTests()
    {
        _handler = new SkipHabitCommandHandler(
            _habitRepo, _habitLogRepo, _userDateService, _unitOfWork, _cache);

        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
    }

    [Fact]
    public async Task Handle_OneTimeTask_PostponesToTomorrow()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "One-time task", null, null,
            DueDate: Today)).Value;
        SetupHabitFound(habit);

        var command = new SkipHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.DueDate.Should().Be(Today.AddDays(1));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_RecurringHabitDueToday_AdvancesDueDate()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Daily habit", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;
        SetupHabitFound(habit);

        var command = new SkipHabitCommand(UserId, habit.Id, Today);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.DueDate.Should().BeAfter(Today);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReturnsFailure()
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Habit?)null);

        var command = new SkipHabitCommand(UserId, Guid.NewGuid());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.HabitNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
    }

    [Fact]
    public async Task Handle_WrongUser_ReturnsFailure()
    {
        var otherUserId = Guid.NewGuid();
        var habit = Habit.Create(new HabitCreateParams(
            otherUserId, "Someone else's habit", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;
        SetupHabitFound(habit);

        var command = new SkipHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.HabitNotOwned);
        result.ErrorCode.Should().Be(ErrorCodes.HabitNotOwned);
    }

    [Fact]
    public async Task Handle_CompletedHabit_ReturnsFailure()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Task", null, null,
            DueDate: Today)).Value;
        // Complete it by logging
        habit.Log(Today);
        SetupHabitFound(habit);

        var command = new SkipHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("completed");
    }

    [Fact]
    public async Task Handle_FutureDate_ReturnsFailure()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Daily", FrequencyUnit.Day, 1,
            DueDate: Today)).Value;
        SetupHabitFound(habit);

        var futureDate = Today.AddDays(5);
        var command = new SkipHabitCommand(UserId, habit.Id, futureDate);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("future");
    }

    [Fact]
    public async Task Handle_NotYetDue_ReturnsFailure()
    {
        var futureDueDate = Today.AddDays(5);
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Future habit", FrequencyUnit.Day, 1,
            DueDate: futureDueDate)).Value;
        SetupHabitFound(habit);

        var command = new SkipHabitCommand(UserId, habit.Id, Today);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not yet due");
    }

    [Fact]
    public async Task Handle_InvalidatesSummaryCache()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Task", null, null,
            DueDate: Today)).Value;
        SetupHabitFound(habit);

        var realToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var cacheKey = $"summary:{UserId}:{realToday:yyyy-MM-dd}:en";
        _cache.Set(cacheKey, "cached-summary");

        var command = new SkipHabitCommand(UserId, habit.Id);

        await _handler.Handle(command, CancellationToken.None);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }

    private void SetupHabitFound(Habit habit)
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);
    }
}
