using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Habits;

public class BulkLogHabitsCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUserStreakService _userStreakService = Substitute.For<IUserStreakService>();
    private readonly IGamificationService _gamificationService = Substitute.For<IGamificationService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly BulkLogHabitsCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public BulkLogHabitsCommandHandlerTests()
    {
        var services = new BulkLogServices(_userDateService, _userStreakService, _gamificationService);
        _handler = new BulkLogHabitsCommandHandler(
            _habitRepo, _habitLogRepo, services, _unitOfWork, _cache,
            Substitute.For<ILogger<BulkLogHabitsCommandHandler>>());

        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
        _userStreakService.RecalculateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new UserStreakState(1, 1, Today));
    }

    [Fact]
    public async Task Handle_MultipleValidHabits_LogsAllSuccessfully()
    {
        var habit1 = Habit.Create(new HabitCreateParams(UserId, "Habit 1", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var habit2 = Habit.Create(new HabitCreateParams(UserId, "Habit 2", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        SetupHabitsForUser(new List<Habit> { habit1, habit2 });

        var items = new List<BulkLogItem>
        {
            new(habit1.Id),
            new(habit2.Id)
        };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().HaveCount(2);
        result.Value.Results.Should().AllSatisfy(r => r.Status.Should().Be(BulkItemStatus.Success));
        await _habitLogRepo.Received(2).AddAsync(Arg.Any<HabitLog>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReportsFailedItem()
    {
        var habit1 = Habit.Create(new HabitCreateParams(UserId, "Habit 1", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var missingId = Guid.NewGuid();

        SetupHabitsForUser(new List<Habit> { habit1 });

        var items = new List<BulkLogItem>
        {
            new(habit1.Id),
            new(missingId)
        };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Success);
        result.Value.Results[1].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[1].Error.Should().Be(ErrorMessages.HabitNotFound);
    }

    [Fact]
    public async Task Handle_FutureDate_ReportsFailedItem()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var futureDate = Today.AddDays(5);
        var items = new List<BulkLogItem> { new(habit.Id, futureDate) };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[0].Error.Should().Contain("future");
    }

    [Fact]
    public async Task Handle_DateBeyondOverdueWindow_ReportsFailedItem()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var tooOldDate = Today.AddDays(-(AppConstants.DefaultOverdueWindowDays + 1));
        var items = new List<BulkLogItem> { new(habit.Id, tooOldDate) };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[0].Error.Should().Contain("overdue window");
    }

    [Fact]
    public async Task Handle_OneTimeTask_LogsAndMarksCompleted()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Task", null, null, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var items = new List<BulkLogItem> { new(habit.Id) };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Success);
        habit.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_CallsGamificationForSuccessfulLogs()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var items = new List<BulkLogItem> { new(habit.Id) };
        var command = new BulkLogHabitsCommand(UserId, items);

        await _handler.Handle(command, CancellationToken.None);

        await _gamificationService.Received(1).ProcessHabitLogged(
            UserId, habit.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidatesSummaryCache()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var realToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var cacheKey = $"summary:{UserId}:{realToday:yyyy-MM-dd}:en";
        _cache.Set(cacheKey, "cached-summary");

        var items = new List<BulkLogItem> { new(habit.Id) };
        var command = new BulkLogHabitsCommand(UserId, items);

        await _handler.Handle(command, CancellationToken.None);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_AlreadyLoggedRecurringHabit_FailsScheduleCheckSinceDueDateAdvanced()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        // Log once - this advances DueDate past Today for recurring habits
        habit.Log(Today);
        SetupHabitsForUser(new List<Habit> { habit });

        var items = new List<BulkLogItem> { new(habit.Id) };
        var command = new BulkLogHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // After logging a daily habit, DueDate advances, so re-logging the same date fails schedule check
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[0].Error.Should().Contain("not scheduled");
    }

    private void SetupHabitsForUser(List<Habit> habits)
    {
        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habits);
    }
}
