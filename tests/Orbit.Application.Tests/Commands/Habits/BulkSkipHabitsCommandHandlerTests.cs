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

public class BulkSkipHabitsCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly BulkSkipHabitsCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public BulkSkipHabitsCommandHandlerTests()
    {
        _handler = new BulkSkipHabitsCommandHandler(
            _habitRepo, _habitLogRepo, _userDateService, _unitOfWork, _cache);

        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
    }

    [Fact]
    public async Task Handle_RecurringHabitsDue_SkipsAndAdvancesDueDate()
    {
        var habit1 = Habit.Create(new HabitCreateParams(UserId, "Habit 1", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var habit2 = Habit.Create(new HabitCreateParams(UserId, "Habit 2", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        SetupHabitsForUser(new List<Habit> { habit1, habit2 });

        var items = new List<BulkSkipItem>
        {
            new(habit1.Id),
            new(habit2.Id)
        };
        var command = new BulkSkipHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().HaveCount(2);
        result.Value.Results.Should().AllSatisfy(r => r.Status.Should().Be(BulkItemStatus.Success));
        habit1.DueDate.Should().BeAfter(Today);
        habit2.DueDate.Should().BeAfter(Today);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_OneTimeTask_PostponesToTomorrow()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Task", null, null, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var items = new List<BulkSkipItem> { new(habit.Id) };
        var command = new BulkSkipHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Success);
        habit.DueDate.Should().Be(Today.AddDays(1));
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReportsFailedItem()
    {
        var missingId = Guid.NewGuid();
        SetupHabitsForUser(new List<Habit>());

        var items = new List<BulkSkipItem> { new(missingId) };
        var command = new BulkSkipHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[0].Error.Should().Be(ErrorMessages.HabitNotFound);
    }

    [Fact]
    public async Task Handle_CompletedHabit_ReportsFailedItem()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Task", null, null, DueDate: Today)).Value;
        habit.Log(Today); // Completes one-time task
        SetupHabitsForUser(new List<Habit> { habit });

        var items = new List<BulkSkipItem> { new(habit.Id) };
        var command = new BulkSkipHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[0].Error.Should().Contain("completed");
    }

    [Fact]
    public async Task Handle_FutureDate_ReportsFailedItem()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var futureDate = Today.AddDays(5);
        var items = new List<BulkSkipItem> { new(habit.Id, futureDate) };
        var command = new BulkSkipHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[0].Error.Should().Contain("future");
    }

    [Fact]
    public async Task Handle_NotYetDue_ReportsFailedItem()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Future habit", FrequencyUnit.Day, 1,
            DueDate: Today.AddDays(5))).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var items = new List<BulkSkipItem> { new(habit.Id, Today) };
        var command = new BulkSkipHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[0].Error.Should().Contain("not yet due");
    }

    [Fact]
    public async Task Handle_InvalidatesSummaryCache()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Task", null, null, DueDate: Today)).Value;
        SetupHabitsForUser(new List<Habit> { habit });

        var realToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var cacheKey = $"summary:{UserId}:{realToday:yyyy-MM-dd}:en";
        _cache.Set(cacheKey, "cached-summary");

        var items = new List<BulkSkipItem> { new(habit.Id) };
        var command = new BulkSkipHabitsCommand(UserId, items);

        await _handler.Handle(command, CancellationToken.None);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_MixedResults_ReportsPartialSuccess()
    {
        var validHabit = Habit.Create(new HabitCreateParams(
            UserId, "Valid", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var completedHabit = Habit.Create(new HabitCreateParams(
            UserId, "Done", null, null, DueDate: Today)).Value;
        completedHabit.Log(Today); // Complete it

        SetupHabitsForUser(new List<Habit> { validHabit, completedHabit });

        var items = new List<BulkSkipItem>
        {
            new(validHabit.Id),
            new(completedHabit.Id),
            new(Guid.NewGuid()) // Not found
        };
        var command = new BulkSkipHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Success);
        result.Value.Results[1].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[2].Status.Should().Be(BulkItemStatus.Failed);
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
