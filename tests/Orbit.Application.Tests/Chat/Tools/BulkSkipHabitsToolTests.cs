using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class BulkSkipHabitsToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly BulkSkipHabitsTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public BulkSkipHabitsToolTests()
    {
        _tool = new BulkSkipHabitsTool(_mediator, _habitRepo, _userDateService,
            new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(),
                Substitute.For<IUnitOfWork>()));
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _mediator.Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var command = call.Arg<BulkSkipHabitsCommand>();
                return Result.Success(new BulkSkipResult(command.Items.Select((item, index) =>
                    new BulkSkipItemResult(index, BulkItemStatus.Success, item.HabitId)).ToList()));
            });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task GeneralMembers_RealHandler_ExposesRejectionAndAccurateCounts(bool mixed, bool revised)
    {
        var general = Habit.Create(new HabitCreateParams(HabitSchedule.General, UserId, "Read", null, null, Today)).Value;
        var dated = CreateHabit("Daily", FrequencyUnit.Day, 1, Today);
        Habit[] habits = mixed ? [general, dated] : [general];
        SetupHabitLookup(habits);
        var before = (general.NextDueDate, general.UpdatedAtUtc);
        var logs = Substitute.For<IGenericRepository<HabitLog>>();
        var unit = Substitute.For<IUnitOfWork>();
        unit.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<bool>>>()(call.Arg<CancellationToken>()));
        _habitRepo.TryRefreshAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>()).Returns(true);
        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(habits.ToList());
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var handler = new BulkSkipHabitsCommandHandler(_habitRepo, logs, _userDateService, unit, cache);
        _mediator.Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<BulkSkipHabitsCommand>(), call.Arg<CancellationToken>()));

        var arguments = revised
            ? JsonSerializer.Serialize(new { revised_items = habits.Select(habit => new { habit_id = habit.Id, date = Today.ToString("yyyy-MM-dd") }) })
            : JsonSerializer.Serialize(new { habit_ids = habits.Select(habit => habit.Id) });
        var result = await Execute(arguments);

        result.Success.Should().Be(mixed);
        if (!mixed)
            result.ErrorCode.Should().Be(DomainErrors.CannotSkipGeneralHabit.Code);
        var payload = JsonSerializer.SerializeToElement(result.Payload);
        payload.GetProperty("applied_count").GetInt32().Should().Be(mixed ? 1 : 0);
        payload.GetProperty("total_matched").GetInt32().Should().Be(habits.Length);
        payload.GetProperty("skipped_count").GetInt32().Should().Be(1);
        var rejected = payload.GetProperty("results").EnumerateArray().Single(item => item.GetProperty("habit_id").GetGuid() == general.Id);
        rejected.GetProperty("error_code").GetString().Should().Be(DomainErrors.CannotSkipGeneralHabit.Code);
        (general.NextDueDate, general.UpdatedAtUtc).Should().Be(before);
        if (mixed)
            dated.NextDueDate.Should().BeAfter(Today);
        await unit.Received(mixed ? 1 : 0).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SkipMultiple_ReturnsSkippedNames()
    {
        var h1 = CreateHabit("Water", FrequencyUnit.Day, 1, Today);
        var h2 = CreateHabit("Exercise", FrequencyUnit.Day, 1, Today);
        SetupHabitLookup(h1, h2);

        var result = await Execute($$$"""{"habit_ids": ["{{{h1.Id}}}", "{{{h2.Id}}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Skipped 2 of 2");
    }

    [Fact]
    public async Task SomeNotFound_SkipsFoundOnes()
    {
        var h1 = CreateHabit("Water", FrequencyUnit.Day, 1, Today);
        var missingId = Guid.NewGuid();
        SetupHabitLookup(h1);

        var result = await Execute($$$"""{"habit_ids": ["{{{h1.Id}}}", "{{{missingId}}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Skipped 1 of 1");
    }

    [Fact]
    public async Task AllNotFound_ReturnsError()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(new List<Habit>());

        var result = await Execute($$$"""{"habit_ids": ["{{{id1}}}", "{{{id2}}}"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No matching habits");
    }

    [Fact]
    public async Task CompletedHabits_SkipsCompleted()
    {
        var completed = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId, "Task", null, null, DueDate: Today)).Value;
        completed.Log(Today);
        var active = CreateHabit("Water", FrequencyUnit.Day, 1, Today);
        SetupHabitLookup(completed, active);
        _mediator.Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new BulkSkipResult([
                new(0, BulkItemStatus.Failed, completed.Id),
                new(1, BulkItemStatus.Success, active.Id)])));

        var result = await Execute($$$"""{"habit_ids": ["{{{completed.Id}}}", "{{{active.Id}}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Skipped 1 of 2");
        result.EntityName.Should().Contain("Partial result");
    }

    [Fact]
    public async Task EmptyIdList_ReturnsError()
    {
        var result = await Execute("""{"habit_ids": []}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_ids");
    }

    [Fact]
    public async Task MissingHabitIds_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("filter or habit_ids");
    }

    [Fact]
    public async Task ConflictingSelectors_ReturnsErrorBeforeLoadingTargets()
    {
        var habitId = Guid.NewGuid();

        var result = await Execute($$$"""{"habit_ids":["{{{habitId}}}"],"filter":{"all":true}}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("cannot combine");
        await _habitRepo.DidNotReceiveWithAnyArgs().FindAsync(default!, default!, default);
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(BulkSkipHabitsCommand)!, default);
    }

    [Fact]
    public async Task OneTimeTask_DefaultsCommandDateToToday()
    {
        var task = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, UserId, "Buy milk", null, null, DueDate: Today)).Value;
        SetupHabitLookup(task);

        var result = await Execute($$$"""{"habit_ids": ["{{{task.Id}}}"]}""");

        result.Success.Should().BeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<BulkSkipHabitsCommand>(command => command.Items.Single().Date == Today),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DifferentUserHabit_IsNotSkipped()
    {
        var otherUserId = Guid.NewGuid();
        var otherUserHabit = Habit.Create(
            new HabitCreateParams(HabitSchedule.Recurring, otherUserId, "Other user habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetupHabitLookup(otherUserHabit);

        var result = await Execute($$$"""{"habit_ids": ["{{{otherUserHabit.Id}}}"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No matching habits");
    }

    [Fact]
    public async Task AllFilter_SkipsEveryMatchAcrossBoundedCommands()
    {
        var habits = Enumerable.Range(1, 205)
            .Select(index => CreateHabit($"Habit {index}", FrequencyUnit.Day, 1, Today))
            .ToArray();
        var chunkSizes = new List<int>();
        SetupHabitLookup(habits);
        _mediator.Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var command = call.Arg<BulkSkipHabitsCommand>();
                chunkSizes.Add(command.Items.Count);
                return Result.Success(new BulkSkipResult(command.Items.Select((item, index) =>
                    new BulkSkipItemResult(index, BulkItemStatus.Success, item.HabitId)).ToList()));
            });

        var result = await Execute("""{"filter":{"all":true}}""");

        result.EntityName.Should().Contain("Skipped 205 of 205");
        chunkSizes.Should().Equal(AppConstants.MaxBulkOperationSize, AppConstants.MaxBulkOperationSize, 5);
    }

    private static Habit CreateHabit(string title, FrequencyUnit? freq, int? qty, DateOnly dueDate)
    {
        return Habit.Create(new HabitCreateParams(((FrequencyUnit?)(freq)) is null ? HabitSchedule.OneTime : HabitSchedule.Recurring, UserId, title, freq, qty, DueDate: dueDate)).Value;
    }

    private void SetupHabitLookup(params Habit[] habits)
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(callInfo =>
        {
            var predicate = callInfo.ArgAt<Expression<Func<Habit, bool>>>(0).Compile();
            return habits.Where(predicate).ToList();
        });
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
