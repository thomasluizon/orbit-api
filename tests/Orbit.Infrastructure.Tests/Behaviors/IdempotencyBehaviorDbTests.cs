using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Behaviors;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Challenges.Services;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Behaviors;

public class IdempotencyBehaviorDbTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly OrbitDbContext _dbContext;
    private readonly UnitOfWork _unitOfWork;
    private readonly IdempotencyStore _store;
    private readonly Guid _userId = Guid.NewGuid();

    private int _handlerCalls;

    public IdempotencyBehaviorDbTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new SqliteCompatOrbitDbContext(options);
        _dbContext.Database.EnsureCreated();

        var user = User.Create("Test User", "idem@example.com").Value;
        typeof(User).GetProperty("Id")!.SetValue(user, _userId);
        _dbContext.Users.Add(user);
        _dbContext.SaveChanges();

        _unitOfWork = new UnitOfWork(_dbContext, new DatabaseConnectionSettings());
        _store = new IdempotencyStore(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Handle_NewKey_ExecutesHandlerOnceAndStoresResponse()
    {
        var behavior = CreateBehavior<FakeRequest, string>();

        var response = await behavior.Handle(new FakeRequest(), CreateTagHandler(), CancellationToken.None);

        response.Should().Be("response-1");
        _handlerCalls.Should().Be(1);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(1);
        (await _dbContext.Tags.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_ReplayedKey_ReturnsStoredResponseWithoutReExecuting()
    {
        var behavior = CreateBehavior<FakeRequest, string>();
        var handler = CreateTagHandler();

        var first = await behavior.Handle(new FakeRequest(), handler, CancellationToken.None);
        var replay = await behavior.Handle(new FakeRequest(), handler, CancellationToken.None);

        first.Should().Be("response-1");
        replay.Should().Be("response-1");
        _handlerCalls.Should().Be(1);
        (await _dbContext.Tags.CountAsync()).Should().Be(1);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_SuccessResultResponse_RoundTripsThroughLedgerOnReplay()
    {
        var behavior = CreateBehavior<ResultRequest, Result<string>>();
        RequestHandlerDelegate<Result<string>> handler = async ct =>
        {
            _handlerCalls++;
            _dbContext.Tags.Add(Tag.Create(_userId, "tag", "#ff0000").Value);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success("created-id");
        };

        var first = await behavior.Handle(new ResultRequest(), handler, CancellationToken.None);
        var replay = await behavior.Handle(new ResultRequest(), handler, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        first.Value.Should().Be("created-id");
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().Be("created-id");
        _handlerCalls.Should().Be(1);
        (await _dbContext.Tags.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_FailureResultResponse_DoesNotCrashAndReplaysTheFailure()
    {
        var behavior = CreateBehavior<ResultRequest, Result<string>>();
        RequestHandlerDelegate<Result<string>> handler = _ =>
        {
            _handlerCalls++;
            return Task.FromResult(Result.Failure<string>("habit not found", "NOT_FOUND"));
        };

        var first = await behavior.Handle(new ResultRequest(), handler, CancellationToken.None);
        var replay = await behavior.Handle(new ResultRequest(), handler, CancellationToken.None);

        first.IsFailure.Should().BeTrue();
        first.Error.Should().Be("habit not found");
        first.ErrorCode.Should().Be("NOT_FOUND");
        replay.IsFailure.Should().BeTrue();
        replay.Error.Should().Be("habit not found");
        replay.ErrorCode.Should().Be("NOT_FOUND");
        _handlerCalls.Should().Be(1);
    }

    [Fact]
    public async Task Handle_NonGenericResultResponse_RoundTripsThroughLedgerOnReplay()
    {
        var behavior = CreateBehavior<PlainResultRequest, Result>();
        RequestHandlerDelegate<Result> handler = _ =>
        {
            _handlerCalls++;
            return Task.FromResult(Result.Success());
        };

        var first = await behavior.Handle(new PlainResultRequest(), handler, CancellationToken.None);
        var replay = await behavior.Handle(new PlainResultRequest(), handler, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        _handlerCalls.Should().Be(1);
    }

    [Fact]
    public async Task Handle_SameKeyDifferentRequestTypes_BothExecute()
    {
        var context = new StubIdempotencyContext(true, _userId, "shared-key");
        var first = new IdempotencyBehavior<FakeRequest, string>(context, _store, _unitOfWork);
        var second = new IdempotencyBehavior<OtherRequest, string>(context, _store, _unitOfWork);

        var firstResponse = await first.Handle(new FakeRequest(), CountingHandler("first"), CancellationToken.None);
        var secondResponse = await second.Handle(new OtherRequest(), CountingHandler("second"), CancellationToken.None);

        firstResponse.Should().Be("first");
        secondResponse.Should().Be("second");
        _handlerCalls.Should().Be(2);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Handle_SameKeyDifferentCommandsOfOneType_BothExecute()
    {
        var context = new StubIdempotencyContext(true, _userId, "mutation-key-1", trackOrdinals: true);
        var behavior = new IdempotencyBehavior<NamedRequest, string>(context, _store, _unitOfWork);

        var first = await behavior.Handle(new NamedRequest("first"), CountingHandler("first"), CancellationToken.None);
        var second = await behavior.Handle(new NamedRequest("second"), CountingHandler("second"), CancellationToken.None);

        context.ResetOrdinals();

        first.Should().Be("first");
        second.Should().Be("second");
        (await behavior.Handle(new NamedRequest("first"), CountingHandler("changed"), CancellationToken.None))
            .Should().Be("first");
        (await behavior.Handle(new NamedRequest("second"), CountingHandler("changed"), CancellationToken.None))
            .Should().Be("second");
        _handlerCalls.Should().Be(2);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Handle_ReplayOfRowWrittenBeforeOrdinalScoping_ReturnsStoredResponse()
    {
        var legacy = ProcessedRequest.Create(
            _userId, "mutation-key-1", typeof(NamedRequest).FullName!, 0);
        legacy.SetResponseBody(JsonSerializer.Serialize("stored-before-deploy"));
        _dbContext.ProcessedRequests.Add(legacy);
        await _dbContext.SaveChangesAsync();
        var behavior = CreateBehavior<NamedRequest, string>();

        var replay = await behavior.Handle(new NamedRequest("first"), CountingHandler("executed-again"), CancellationToken.None);

        replay.Should().Be("stored-before-deploy");
        _handlerCalls.Should().Be(0);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_SharedKey250Habits_AllChunksApplyOnceAndReplay(bool skip)
    {
        var today = new DateOnly(2026, 9, 24);
        var habits = Enumerable.Range(1, 250).Select(index => Habit.Create(new HabitCreateParams(
            _userId, $"Habit {index}", skip ? FrequencyUnit.Week : FrequencyUnit.Day,
            skip ? 3 : 1, DueDate: today, IsFlexible: skip)).Value).ToArray();
        _dbContext.Habits.AddRange(habits);
        await _dbContext.SaveChangesAsync();

        var dateService = CreateUserDateService(today);
        var mediator = Substitute.For<IMediator>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var habitRepository = new GenericRepository<Habit>(_dbContext);
        var logRepository = new GenericRepository<HabitLog>(_dbContext);
        var context = new StubIdempotencyContext(true, _userId, "shared-http-key", trackOrdinals: true);
        if (skip)
        {
            var handler = new BulkSkipHabitsCommandHandler(habitRepository, logRepository, dateService, _unitOfWork, cache);
            var behavior = new IdempotencyBehavior<BulkSkipHabitsCommand, Result<BulkSkipResult>>(context, _store, _unitOfWork);
            mediator.Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var command = call.Arg<BulkSkipHabitsCommand>();
                    return behavior.Handle(command, ct =>
                    {
                        _handlerCalls++;
                        return handler.Handle(command, ct);
                    }, call.Arg<CancellationToken>());
                });
        }
        else
        {
            var goalService = Substitute.For<IGoalCompletionService>();
            goalService.SyncDerivedGoalsAsync(_userId, Arg.Any<IReadOnlyCollection<Guid>>(), today,
                    Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<GoalCompletionUpdate>)Array.Empty<GoalCompletionUpdate>());
            var handler = new BulkLogHabitsCommandHandler(habitRepository, logRepository,
                new BulkLogServices(dateService, Substitute.For<IUserStreakService>(), Substitute.For<IGamificationService>()),
                goalService, _unitOfWork, cache, NullLogger<BulkLogHabitsCommandHandler>.Instance);
            var behavior = new IdempotencyBehavior<BulkLogHabitsCommand, Result<BulkLogResult>>(context, _store, _unitOfWork);
            mediator.Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var command = call.Arg<BulkLogHabitsCommand>();
                    return behavior.Handle(command, ct =>
                    {
                        _handlerCalls++;
                        return handler.Handle(command, ct);
                    }, call.Arg<CancellationToken>());
                });
        }

        var args = JsonSerializer.SerializeToElement(new { habit_ids = habits.Select(habit => habit.Id).ToArray() });
        var tool = skip
            ? (Orbit.Application.Chat.Tools.IAiTool)new BulkSkipHabitsTool(mediator, habitRepository, dateService,
                new BulkHabitReplayPlanner(context, _store, _unitOfWork))
            : new BulkLogHabitsTool(mediator, habitRepository, dateService,
                new BulkHabitReplayPlanner(context, _store, _unitOfWork));
        var first = await tool.ExecuteAsync(args, _userId, CancellationToken.None);
        context.ResetOrdinals();
        var replay = await tool.ExecuteAsync(args, _userId, CancellationToken.None);

        first.Success.Should().BeTrue();
        first.EntityName.Should().Contain("250 of 250");
        replay.EntityName.Should().Be(first.EntityName);
        JsonSerializer.Serialize(replay.Payload).Should().Be(JsonSerializer.Serialize(first.Payload));
        _handlerCalls.Should().Be(3);
        (await _dbContext.HabitLogs.AsNoTracking().CountAsync()).Should().Be(250);
        (await _dbContext.HabitLogs.AsNoTracking().Select(log => log.HabitId).Distinct().CountAsync()).Should().Be(250);
        (await _dbContext.HabitLogs.AsNoTracking().CountAsync(log => log.Value == 0)).Should().Be(skip ? 250 : 0);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_FirstChunkCommittedThenSelectionShrinks_RetryProcessesRemainingHabits(bool skip)
    {
        var today = new DateOnly(2026, 9, 24);
        var habits = Enumerable.Range(1, 250).Select(index => Habit.Create(new HabitCreateParams(
            _userId, $"Habit {index}", null, null, DueDate: today)).Value).ToArray();
        _dbContext.Habits.AddRange(habits);
        await _dbContext.SaveChangesAsync();

        var dateService = CreateUserDateService(today);
        var mediator = Substitute.For<IMediator>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var habitRepository = new GenericRepository<Habit>(_dbContext);
        var logRepository = new GenericRepository<HabitLog>(_dbContext);
        var context = new StubIdempotencyContext(true, _userId, "partial-http-key", trackOrdinals: true);
        var commandCalls = 0;
        var firstChunkIds = new HashSet<Guid>();
        var interrupt = true;
        if (skip)
        {
            var handler = new BulkSkipHabitsCommandHandler(habitRepository, logRepository, dateService, _unitOfWork, cache);
            var behavior = new IdempotencyBehavior<BulkSkipHabitsCommand, Result<BulkSkipResult>>(context, _store, _unitOfWork);
            mediator.Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var command = call.Arg<BulkSkipHabitsCommand>();
                    if (commandCalls == 0)
                        firstChunkIds.UnionWith(command.Items.Select(item => item.HabitId));
                    if (++commandCalls == 2 && interrupt)
                        return Task.FromException<Result<BulkSkipResult>>(new OperationCanceledException());
                    return behavior.Handle(command, ct =>
                    {
                        _handlerCalls++;
                        return handler.Handle(command, ct);
                    }, call.Arg<CancellationToken>());
                });
        }
        else
        {
            var goalService = Substitute.For<IGoalCompletionService>();
            goalService.SyncDerivedGoalsAsync(_userId, Arg.Any<IReadOnlyCollection<Guid>>(), today,
                    Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<GoalCompletionUpdate>)Array.Empty<GoalCompletionUpdate>());
            var handler = new BulkLogHabitsCommandHandler(habitRepository, logRepository,
                new BulkLogServices(dateService, Substitute.For<IUserStreakService>(), Substitute.For<IGamificationService>()),
                goalService, _unitOfWork, cache, NullLogger<BulkLogHabitsCommandHandler>.Instance);
            var behavior = new IdempotencyBehavior<BulkLogHabitsCommand, Result<BulkLogResult>>(context, _store, _unitOfWork);
            mediator.Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var command = call.Arg<BulkLogHabitsCommand>();
                    if (commandCalls == 0)
                        firstChunkIds.UnionWith(command.Items.Select(item => item.HabitId));
                    if (++commandCalls == 2 && interrupt)
                        return Task.FromException<Result<BulkLogResult>>(new OperationCanceledException());
                    return behavior.Handle(command, ct =>
                    {
                        _handlerCalls++;
                        return handler.Handle(command, ct);
                    }, call.Arg<CancellationToken>());
                });
        }

        var args = JsonSerializer.SerializeToElement(new { filter = new { all = true } });
        var tool = skip
            ? (Orbit.Application.Chat.Tools.IAiTool)new BulkSkipHabitsTool(mediator, habitRepository, dateService,
                new BulkHabitReplayPlanner(context, _store, _unitOfWork))
            : new BulkLogHabitsTool(mediator, habitRepository, dateService,
                new BulkHabitReplayPlanner(context, _store, _unitOfWork));
        await FluentActions.Invoking(() => tool.ExecuteAsync(args, _userId, CancellationToken.None))
            .Should().ThrowAsync<OperationCanceledException>();
        _handlerCalls.Should().Be(1);
        interrupt = false;
        if (skip)
        {
            foreach (var habit in habits.Where(habit => firstChunkIds.Contains(habit.Id)))
                _dbContext.HabitLogs.Add(habit.Log(today).Value);
            await _dbContext.SaveChangesAsync();
        }
        context.ResetOrdinals();
        var replay = await tool.ExecuteAsync(args, _userId, CancellationToken.None);

        replay.Success.Should().BeTrue();
        replay.EntityName.Should().Contain(skip ? "Skipped 250 of 250" : "Logged 250 of 250");
        var payload = JsonSerializer.SerializeToElement(replay.Payload);
        payload.GetProperty("applied_count").GetInt32().Should().Be(250);
        payload.GetProperty("total_matched").GetInt32().Should().Be(250);
        _handlerCalls.Should().Be(3);
        (await _dbContext.Habits.AsNoTracking().CountAsync(habit => habit.DueDate > today || habit.IsCompleted))
            .Should().Be(250);
        (await _dbContext.HabitLogs.AsNoTracking().Select(log => log.HabitId).Distinct().CountAsync())
            .Should().Be(skip ? 100 : 250);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_PlanCommittedWithoutChunks_RetryExecutesEveryChunkOnce(bool skip)
    {
        var today = new DateOnly(2026, 9, 24);
        var habits = Enumerable.Range(1, 250).Select(index => Habit.Create(new HabitCreateParams(
            _userId, $"Habit {index}", FrequencyUnit.Day, 1, DueDate: today)).Value).ToArray();
        _dbContext.Habits.AddRange(habits);
        await _dbContext.SaveChangesAsync();

        var dateService = CreateUserDateService(today);
        var mediator = Substitute.For<IMediator>();
        var habitRepository = new GenericRepository<Habit>(_dbContext);
        var context = new StubIdempotencyContext(true, _userId, "plan-only-key", trackOrdinals: true);
        var planner = new BulkHabitReplayPlanner(context, _store, _unitOfWork);
        var firstAttempt = true;
        var processedIds = new List<Guid>();
        if (skip)
        {
            mediator.Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    if (firstAttempt)
                        return Task.FromException<Result<BulkSkipResult>>(new OperationCanceledException());
                    var items = call.Arg<BulkSkipHabitsCommand>().Items;
                    processedIds.AddRange(items.Select(item => item.HabitId));
                    return Task.FromResult(Result.Success(new BulkSkipResult(items.Select((item, index) =>
                        new BulkSkipItemResult(index, BulkItemStatus.Success, item.HabitId)).ToArray())));
                });
        }
        else
        {
            mediator.Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    if (firstAttempt)
                        return Task.FromException<Result<BulkLogResult>>(new OperationCanceledException());
                    var items = call.Arg<BulkLogHabitsCommand>().Items;
                    processedIds.AddRange(items.Select(item => item.HabitId));
                    return Task.FromResult(Result.Success(new BulkLogResult(items.Select((item, index) =>
                        new BulkLogItemResult(index, BulkItemStatus.Success, item.HabitId, Guid.NewGuid())).ToArray())));
                });
        }

        var tool = skip
            ? (Orbit.Application.Chat.Tools.IAiTool)new BulkSkipHabitsTool(mediator, habitRepository, dateService, planner)
            : new BulkLogHabitsTool(mediator, habitRepository, dateService, planner);
        var args = JsonSerializer.SerializeToElement(new { filter = new { all = true } });
        await FluentActions.Invoking(() => tool.ExecuteAsync(args, _userId, CancellationToken.None))
            .Should().ThrowAsync<OperationCanceledException>();
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(1);

        firstAttempt = false;
        context.ResetOrdinals();
        var replay = await tool.ExecuteAsync(args, _userId, CancellationToken.None);

        replay.Success.Should().BeTrue();
        replay.EntityName.Should().Contain(skip ? "Skipped 250 of 250" : "Logged 250 of 250");
        processedIds.Should().HaveCount(250).And.OnlyHaveUniqueItems();
        processedIds.Should().BeEquivalentTo(habits.Select(habit => habit.Id));
        if (skip)
            await mediator.Received(4).Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>());
        else
            await mediator.Received(4).Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>());
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_ChatRetryAfterLocalMidnight_ReplaysOriginalBulkLog()
    {
        var firstDay = new DateOnly(2026, 9, 24);
        var currentDay = firstDay;
        var habit = Habit.Create(new HabitCreateParams(
            _userId, "Daily habit", FrequencyUnit.Day, 1, DueDate: firstDay)).Value;
        _dbContext.Habits.Add(habit);
        await _dbContext.SaveChangesAsync();

        var dateService = Substitute.For<IUserDateService>();
        dateService.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(_ => currentDay);
        dateService.GetUserWeekStartDayAsync(_userId, Arg.Any<CancellationToken>()).Returns(1);
        var mediator = Substitute.For<IMediator>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var habitRepository = new GenericRepository<Habit>(_dbContext);
        var goalService = Substitute.For<IGoalCompletionService>();
        goalService.SyncDerivedGoalsAsync(_userId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<GoalCompletionUpdate>)Array.Empty<GoalCompletionUpdate>());
        var handler = new BulkLogHabitsCommandHandler(habitRepository, new GenericRepository<HabitLog>(_dbContext),
            new BulkLogServices(dateService, Substitute.For<IUserStreakService>(), Substitute.For<IGamificationService>()),
            goalService, _unitOfWork, cache, NullLogger<BulkLogHabitsCommandHandler>.Instance);
        var context = new StubIdempotencyContext(true, _userId, "chat-retry", trackOrdinals: true);
        var behavior = new IdempotencyBehavior<BulkLogHabitsCommand, Result<BulkLogResult>>(
            context, _store, _unitOfWork);
        mediator.Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var command = call.Arg<BulkLogHabitsCommand>();
                return behavior.Handle(command, ct =>
                {
                    _handlerCalls++;
                    return handler.Handle(command, ct);
                }, call.Arg<CancellationToken>());
            });
        var tool = new BulkLogHabitsTool(mediator, habitRepository, dateService,
            new BulkHabitReplayPlanner(context, _store, _unitOfWork));
        var args = JsonSerializer.SerializeToElement(new { habit_ids = new[] { habit.Id } });

        var first = await tool.ExecuteAsync(args, _userId, CancellationToken.None);
        currentDay = firstDay.AddDays(1);
        context.ResetOrdinals();
        var replay = await tool.ExecuteAsync(args, _userId, CancellationToken.None);

        first.Success.Should().BeTrue();
        replay.EntityName.Should().Be(first.EntityName);
        JsonSerializer.Serialize(replay.Payload).Should().Be(JsonSerializer.Serialize(first.Payload));
        _handlerCalls.Should().Be(1);
        (await _dbContext.HabitLogs.AsNoTracking().Select(log => log.Date).ToListAsync())
            .Should().BeEquivalentTo([firstDay]);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_DifferentBulkInvocationsReversed_ChangesEachHabitOnce(bool skip)
    {
        var today = new DateOnly(2026, 9, 24);
        var habits = new[]
        {
            Habit.Create(new HabitCreateParams(_userId, "First", FrequencyUnit.Week, 3,
                DueDate: today, IsFlexible: true)).Value,
            Habit.Create(new HabitCreateParams(_userId, "Second", FrequencyUnit.Week, 3,
                DueDate: today, IsFlexible: true)).Value
        };
        _dbContext.Habits.AddRange(habits);
        await _dbContext.SaveChangesAsync();
        var mediator = Substitute.For<IMediator>();
        var sentIds = new List<Guid>();
        var context = new StubIdempotencyContext(true, _userId, "reversed-invocations", trackOrdinals: true);
        if (skip)
        {
            var behavior = new IdempotencyBehavior<BulkSkipHabitsCommand, Result<BulkSkipResult>>(
                context, _store, _unitOfWork);
            mediator.Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var command = call.Arg<BulkSkipHabitsCommand>();
                    sentIds.Add(command.Items.Single().HabitId);
                    return behavior.Handle(command, async ct =>
                    {
                        _handlerCalls++;
                        var item = command.Items.Single();
                        var habit = habits.Single(candidate => candidate.Id == item.HabitId);
                        _dbContext.HabitLogs.Add(habit.SkipFlexible(item.Date!.Value).Value);
                        await _unitOfWork.SaveChangesAsync(ct);
                        return Result.Success(new BulkSkipResult(
                            [new BulkSkipItemResult(0, BulkItemStatus.Success, item.HabitId)]));
                    }, call.Arg<CancellationToken>());
                });
        }
        else
        {
            var behavior = new IdempotencyBehavior<BulkLogHabitsCommand, Result<BulkLogResult>>(
                context, _store, _unitOfWork);
            mediator.Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var command = call.Arg<BulkLogHabitsCommand>();
                    sentIds.Add(command.Items.Single().HabitId);
                    return behavior.Handle(command, async ct =>
                    {
                        _handlerCalls++;
                        var item = command.Items.Single();
                        var habit = habits.Single(candidate => candidate.Id == item.HabitId);
                        var log = habit.Log(item.Date!.Value).Value;
                        _dbContext.HabitLogs.Add(log);
                        await _unitOfWork.SaveChangesAsync(ct);
                        return Result.Success(new BulkLogResult(
                            [new BulkLogItemResult(0, BulkItemStatus.Success, item.HabitId, log.Id)]));
                    }, call.Arg<CancellationToken>());
                });
        }
        var repository = new GenericRepository<Habit>(_dbContext);
        var dateService = CreateUserDateService(today);
        var planner = new BulkHabitReplayPlanner(context, _store, _unitOfWork);
        var tool = skip
            ? (Orbit.Application.Chat.Tools.IAiTool)new BulkSkipHabitsTool(mediator, repository, dateService, planner)
            : new BulkLogHabitsTool(mediator, repository, dateService, planner);
        var firstArgs = JsonSerializer.SerializeToElement(new { habit_ids = new[] { habits[0].Id } });
        var secondArgs = JsonSerializer.SerializeToElement(new { habit_ids = new[] { habits[1].Id } });

        (await tool.ExecuteAsync(firstArgs, _userId, CancellationToken.None)).Success.Should().BeTrue();
        (await tool.ExecuteAsync(secondArgs, _userId, CancellationToken.None)).Success.Should().BeTrue();
        context.ResetOrdinals();
        (await tool.ExecuteAsync(secondArgs, _userId, CancellationToken.None)).Success.Should().BeTrue();
        (await tool.ExecuteAsync(firstArgs, _userId, CancellationToken.None)).Success.Should().BeTrue();

        sentIds.Should().Equal(habits[0].Id, habits[1].Id, habits[0].Id, habits[1].Id);
        _handlerCalls.Should().Be(2);
        (await _dbContext.HabitLogs.AsNoTracking().Select(log => log.HabitId).ToListAsync())
            .Should().BeEquivalentTo(habits.Select(habit => habit.Id));
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(4);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_ChangedBulkArguments_ReplaysOriginalSelectionAndDate(bool skip, bool changeDate)
    {
        var firstDay = new DateOnly(2026, 9, 24);
        var original = Enumerable.Range(1, 205).Select(index => Habit.Create(new HabitCreateParams(
            _userId, $"Original {index}", FrequencyUnit.Week, 3, DueDate: firstDay, IsFlexible: true)).Value).ToArray();
        var outside = Habit.Create(new HabitCreateParams(
            _userId, "Outside", FrequencyUnit.Week, 3, DueDate: firstDay, IsFlexible: true)).Value;
        _dbContext.Habits.AddRange([.. original, outside]);
        await _dbContext.SaveChangesAsync();

        var context = new StubIdempotencyContext(true, _userId, "changed-arguments", trackOrdinals: true);
        var mediator = Substitute.For<IMediator>();
        var commandCalls = 0;
        var interrupt = true;
        if (skip)
        {
            var behavior = new IdempotencyBehavior<BulkSkipHabitsCommand, Result<BulkSkipResult>>(
                context, _store, _unitOfWork);
            mediator.Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var command = call.Arg<BulkSkipHabitsCommand>();
                    if (++commandCalls == 2 && interrupt)
                        return Task.FromException<Result<BulkSkipResult>>(new OperationCanceledException());
                    return behavior.Handle(command, async ct =>
                    {
                        _handlerCalls++;
                        var results = new List<BulkSkipItemResult>();
                        foreach (var (item, index) in command.Items.Select((item, index) => (item, index)))
                        {
                            var habit = original.Append(outside).Single(candidate => candidate.Id == item.HabitId);
                            _dbContext.HabitLogs.Add(habit.SkipFlexible(item.Date!.Value).Value);
                            results.Add(new BulkSkipItemResult(index, BulkItemStatus.Success, item.HabitId));
                        }
                        await _unitOfWork.SaveChangesAsync(ct);
                        return Result.Success(new BulkSkipResult(results));
                    }, call.Arg<CancellationToken>());
                });
        }
        else
        {
            var behavior = new IdempotencyBehavior<BulkLogHabitsCommand, Result<BulkLogResult>>(
                context, _store, _unitOfWork);
            mediator.Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var command = call.Arg<BulkLogHabitsCommand>();
                    if (++commandCalls == 2 && interrupt)
                        return Task.FromException<Result<BulkLogResult>>(new OperationCanceledException());
                    return behavior.Handle(command, async ct =>
                    {
                        _handlerCalls++;
                        var results = new List<BulkLogItemResult>();
                        foreach (var (item, index) in command.Items.Select((item, index) => (item, index)))
                        {
                            var habit = original.Append(outside).Single(candidate => candidate.Id == item.HabitId);
                            var log = habit.Log(item.Date!.Value).Value;
                            _dbContext.HabitLogs.Add(log);
                            results.Add(new BulkLogItemResult(index, BulkItemStatus.Success, item.HabitId, log.Id));
                        }
                        await _unitOfWork.SaveChangesAsync(ct);
                        return Result.Success(new BulkLogResult(results));
                    }, call.Arg<CancellationToken>());
                });
        }

        var dateService = CreateUserDateService(firstDay.AddDays(1));
        var repository = new GenericRepository<Habit>(_dbContext);
        var planner = new BulkHabitReplayPlanner(context, _store, _unitOfWork);
        var tool = skip
            ? (Orbit.Application.Chat.Tools.IAiTool)new BulkSkipHabitsTool(mediator, repository, dateService, planner)
            : new BulkLogHabitsTool(mediator, repository, dateService, planner);
        var firstArgs = JsonSerializer.SerializeToElement(new
        {
            filter = new { search = "Original" }, date = firstDay.ToString("yyyy-MM-dd")
        });
        var changedArgs = changeDate
            ? JsonSerializer.SerializeToElement(new
            {
                filter = new { search = "Original" }, date = firstDay.AddDays(1).ToString("yyyy-MM-dd")
            })
            : JsonSerializer.SerializeToElement(new
            {
                filter = new { search = "Outside" }, date = firstDay.ToString("yyyy-MM-dd")
            });

        await FluentActions.Invoking(() => tool.ExecuteAsync(firstArgs, _userId, CancellationToken.None))
            .Should().ThrowAsync<OperationCanceledException>();
        (await _dbContext.HabitLogs.AsNoTracking().CountAsync()).Should().Be(100);
        interrupt = false;
        context.ResetOrdinals();
        var replay = await tool.ExecuteAsync(changedArgs, _userId, CancellationToken.None);

        replay.Success.Should().BeTrue();
        replay.EntityName.Should().Contain($"205 of 205");
        _handlerCalls.Should().Be(3);
        (await _dbContext.HabitLogs.AsNoTracking().CountAsync()).Should().Be(205);
        (await _dbContext.HabitLogs.AsNoTracking().Select(log => log.HabitId).ToListAsync())
            .Should().BeEquivalentTo(original.Select(habit => habit.Id));
        (await _dbContext.HabitLogs.AsNoTracking().Select(log => log.Date).Distinct().ToListAsync())
            .Should().BeEquivalentTo([firstDay]);
    }

    [Fact]
    public async Task Handle_IdenticalBulkLogInvocations_ApplyTwiceAndReplayByPosition()
    {
        var today = new DateOnly(2026, 9, 24);
        var habit = Habit.Create(new HabitCreateParams(_userId, "Repeated", FrequencyUnit.Week, 2,
            DueDate: today, IsFlexible: true)).Value;
        _dbContext.Habits.Add(habit);
        await _dbContext.SaveChangesAsync();
        var dateService = CreateUserDateService(today);
        var streakService = Substitute.For<IUserStreakService>();
        var gamificationService = Substitute.For<IGamificationService>();
        var goalService = Substitute.For<IGoalCompletionService>();
        goalService.SyncDerivedGoalsAsync(
                _userId, Arg.Any<IReadOnlyCollection<Guid>>(), today,
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<GoalCompletionUpdate>)Array.Empty<GoalCompletionUpdate>());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new BulkLogHabitsCommandHandler(
            new GenericRepository<Habit>(_dbContext), new GenericRepository<HabitLog>(_dbContext),
            new BulkLogServices(dateService, streakService, gamificationService),
            goalService, _unitOfWork, cache, NullLogger<BulkLogHabitsCommandHandler>.Instance);
        var context = new StubIdempotencyContext(true, _userId, "identical-invocations", trackOrdinals: true);
        var behavior = new IdempotencyBehavior<BulkLogHabitsCommand, Result<BulkLogResult>>(
            context, _store, _unitOfWork);
        var command = new BulkLogHabitsCommand(_userId, [new BulkLogItem(habit.Id, today)]);
        RequestHandlerDelegate<Result<BulkLogResult>> next = ct =>
        {
            _handlerCalls++;
            return handler.Handle(command, ct);
        };

        var first = await behavior.Handle(command, next, CancellationToken.None);
        var second = await behavior.Handle(command, next, CancellationToken.None);

        first.Value.Results.Should().ContainSingle(item => item.Status == BulkItemStatus.Success && item.LogId != null);
        second.Value.Results.Should().ContainSingle(item => item.Status == BulkItemStatus.Success && item.LogId != null);
        second.Value.Results.Single().LogId!.Value.Should().NotBe(first.Value.Results.Single().LogId!.Value);
        _handlerCalls.Should().Be(2);
        (await _dbContext.HabitLogs.AsNoTracking().CountAsync(log => log.HabitId == habit.Id && log.Value == 1))
            .Should().Be(2);
        context.ResetOrdinals();
        var firstReplay = await behavior.Handle(command, next, CancellationToken.None);
        var secondReplay = await behavior.Handle(command, next, CancellationToken.None);

        firstReplay.Value.Results.Should().BeEquivalentTo(first.Value.Results);
        secondReplay.Value.Results.Should().BeEquivalentTo(second.Value.Results);
        _handlerCalls.Should().Be(2);
        (await _dbContext.HabitLogs.AsNoTracking().CountAsync(log => log.HabitId == habit.Id && log.Value == 1))
            .Should().Be(2);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Handle_IdenticalBulkSkipInvocations_ApplyTwiceAndReplayByPosition()
    {
        var today = new DateOnly(2026, 9, 24);
        var habit = Habit.Create(new HabitCreateParams(_userId, "Repeated skip", FrequencyUnit.Week, 2,
            DueDate: today, IsFlexible: true)).Value;
        _dbContext.Habits.Add(habit);
        await _dbContext.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new BulkSkipHabitsCommandHandler(
            new GenericRepository<Habit>(_dbContext), new GenericRepository<HabitLog>(_dbContext),
            CreateUserDateService(today), _unitOfWork, cache);
        var context = new StubIdempotencyContext(true, _userId, "identical-skip-invocations", trackOrdinals: true);
        var behavior = new IdempotencyBehavior<BulkSkipHabitsCommand, Result<BulkSkipResult>>(
            context, _store, _unitOfWork);
        var command = new BulkSkipHabitsCommand(_userId, [new BulkSkipItem(habit.Id, today)]);
        RequestHandlerDelegate<Result<BulkSkipResult>> next = ct =>
        {
            _handlerCalls++;
            return handler.Handle(command, ct);
        };

        var first = await behavior.Handle(command, next, CancellationToken.None);
        var second = await behavior.Handle(command, next, CancellationToken.None);

        first.Value.Results.Should().ContainSingle(item => item.Status == BulkItemStatus.Success);
        second.Value.Results.Should().ContainSingle(item => item.Status == BulkItemStatus.Success);
        _handlerCalls.Should().Be(2);
        (await _dbContext.HabitLogs.AsNoTracking().CountAsync(log => log.HabitId == habit.Id && log.Value == 0))
            .Should().Be(2);
        context.ResetOrdinals();
        var firstReplay = await behavior.Handle(command, next, CancellationToken.None);
        var secondReplay = await behavior.Handle(command, next, CancellationToken.None);

        firstReplay.Value.Results.Should().BeEquivalentTo(first.Value.Results);
        secondReplay.Value.Results.Should().BeEquivalentTo(second.Value.Results);
        _handlerCalls.Should().Be(2);
        (await _dbContext.HabitLogs.AsNoTracking().CountAsync(log => log.HabitId == habit.Id && log.Value == 0))
            .Should().Be(2);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_LegacyChatBulkRequest_RefusesWithoutChangingHabits(bool skip, bool afterMidnight)
    {
        var firstDay = new DateOnly(2026, 9, 24);
        var currentDay = afterMidnight ? firstDay.AddDays(1) : firstDay;
        var habits = new[]
        {
            Habit.Create(new HabitCreateParams(_userId, "Committed", FrequencyUnit.Week, 3,
                DueDate: firstDay, IsFlexible: true)).Value,
            Habit.Create(new HabitCreateParams(_userId, "Untouched", FrequencyUnit.Week, 3,
                DueDate: firstDay, IsFlexible: true)).Value
        };
        _dbContext.Habits.AddRange(habits);
        _dbContext.HabitLogs.Add(skip ? habits[0].SkipFlexible(firstDay).Value : habits[0].Log(firstDay).Value);
        var commandType = skip ? typeof(BulkSkipHabitsCommand).FullName! : typeof(BulkLogHabitsCommand).FullName!;
        var legacy = ProcessedRequest.Create(_userId, "legacy-chat-key", commandType, 0);
        legacy.SetResponseBody("committed response");
        _dbContext.ProcessedRequests.Add(legacy);
        await _dbContext.SaveChangesAsync();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Result.Success(new BulkLogResult(
                call.Arg<BulkLogHabitsCommand>().Items.Select((item, index) =>
                    new BulkLogItemResult(index, BulkItemStatus.Success, item.HabitId, Guid.NewGuid())).ToArray()))));
        mediator.Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Result.Success(new BulkSkipResult(
                call.Arg<BulkSkipHabitsCommand>().Items.Select((item, index) =>
                    new BulkSkipItemResult(index, BulkItemStatus.Success, item.HabitId)).ToArray()))));
        var context = new StubIdempotencyContext(true, _userId, "legacy-chat-key", trackOrdinals: true);
        var repository = new GenericRepository<Habit>(_dbContext);
        var dateService = CreateUserDateService(currentDay);
        var planner = new BulkHabitReplayPlanner(context, _store, _unitOfWork);
        var tool = skip
            ? (Orbit.Application.Chat.Tools.IAiTool)new BulkSkipHabitsTool(mediator, repository, dateService, planner)
            : new BulkLogHabitsTool(mediator, repository, dateService, planner);
        var args = JsonSerializer.SerializeToElement(new { filter = new { all = true } });

        var result = await tool.ExecuteAsync(args, _userId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("earlier server version");
        result.Error.Should().Contain("Check the habits and repeat the request");
        (await _dbContext.HabitLogs.AsNoTracking().CountAsync()).Should().Be(1);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(1);
        if (skip)
            await mediator.DidNotReceive().Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>());
        else
            await mediator.DidNotReceive().Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UnmarkedRequest_BypassesLedgerEvenWithKey()
    {
        var behavior = new IdempotencyBehavior<UnmarkedRequest, string>(
            new StubIdempotencyContext(true, _userId, "mutation-key-1"), _store, _unitOfWork);

        var response = await behavior.Handle(new UnmarkedRequest(), CountingHandler("value"), CancellationToken.None);

        response.Should().Be("value");
        _handlerCalls.Should().Be(1);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_NoIdempotencyKey_BypassesLedgerAndRunsHandler()
    {
        var behavior = CreateBehavior<FakeRequest, string>(hasKey: false);

        var response = await behavior.Handle(new FakeRequest(), CreateTagHandler(), CancellationToken.None);

        response.Should().Be("response-1");
        _handlerCalls.Should().Be(1);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_HandlerThrows_RollsBackReservationAndMutationTogether()
    {
        var behavior = CreateBehavior<FakeRequest, string>();
        RequestHandlerDelegate<string> throwingHandler = async ct =>
        {
            _dbContext.Tags.Add(Tag.Create(_userId, "doomed-tag", "#ff0000").Value);
            await _unitOfWork.SaveChangesAsync(ct);
            throw new InvalidOperationException("handler failed after a partial write");
        };

        var act = () => behavior.Handle(new FakeRequest(), throwingHandler, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _dbContext.Tags.CountAsync()).Should().Be(0);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_ConcurrentHabitLog_StoresWinnerAndReplaysWithoutLosingSideEffects()
    {
        var today = new DateOnly(2026, 9, 24);
        var habit = Habit.Create(new HabitCreateParams(
            _userId, "Race habit", FrequencyUnit.Day, 1, DueDate: today)).Value;
        _dbContext.Habits.Add(habit);
        await _dbContext.SaveChangesAsync();

        var userDateService = Substitute.For<IUserDateService>();
        userDateService.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(today);
        var streakService = Substitute.For<IUserStreakService>();
        var gamificationService = Substitute.For<IGamificationService>();
        var challengeService = Substitute.For<IChallengeProgressService>();
        var goalService = Substitute.For<IGoalCompletionService>();
        var mediator = Substitute.For<IMediator>();
        var payGate = Substitute.For<IPayGateService>();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new LogHabitCommandHandler(
            new LogHabitRepositories(
                new GenericRepository<Habit>(_dbContext),
                new GenericRepository<HabitLog>(_dbContext),
                new GenericRepository<User>(_dbContext)),
            new LogHabitServices(userDateService, streakService, gamificationService,
                goalService, challengeService, mediator, payGate),
            _unitOfWork, cache, NullLogger<LogHabitCommandHandler>.Instance);

        var winnerId = Guid.NewGuid();
        goalService.SyncDerivedGoalsAsync(
                _userId, Arg.Any<IReadOnlyCollection<Guid>>(), today,
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var ct = call.ArgAt<CancellationToken>(4);
                var now = DateTime.UtcNow;
                await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO \"HabitLogs\" (\"Id\", \"HabitId\", \"Date\", \"Value\", \"CreatedAtUtc\", \"UpdatedAtUtc\", \"IsDeleted\", \"IsSlip\") VALUES ({winnerId}, {habit.Id}, {today}, {1m}, {now}, {now}, {false}, {false})", ct);
                _dbContext.Tags.Add(Tag.Create(_userId, "losing reward", "#ff0000").Value);
                try
                {
                    await _unitOfWork.SaveChangesAsync(ct);
                }
                catch (DbUpdateException exception) when (exception.InnerException is SqliteException)
                {
                    throw new DbUpdateException("duplicate", new FakeUniqueViolationException());
                }

                return (IReadOnlyList<GoalCompletionUpdate>)Array.Empty<GoalCompletionUpdate>();
            });

        var behavior = CreateBehavior<LogHabitCommand, Result<LogHabitResponse>>();
        var command = new LogHabitCommand(_userId, habit.Id);
        RequestHandlerDelegate<Result<LogHabitResponse>> next = ct =>
        {
            _handlerCalls++;
            return handler.Handle(command, ct);
        };

        var first = await behavior.Handle(command, next, CancellationToken.None);
        var storedBody = await _dbContext.ProcessedRequests
            .AsNoTracking().Select(record => record.ResponseBody).SingleAsync();
        var replay = await behavior.Handle(command, next, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        first.Value.LogId.Should().Be(winnerId);
        first.Value.IsFirstCompletionToday.Should().BeFalse();
        storedBody.Should().NotBeNullOrEmpty();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().BeEquivalentTo(first.Value);
        _handlerCalls.Should().Be(1);
        (await _dbContext.HabitLogs.AsNoTracking().CountAsync()).Should().Be(1);
        (await _dbContext.Tags.AsNoTracking().CountAsync()).Should().Be(0);
        (await _dbContext.Habits.AsNoTracking().SingleAsync()).DueDate.Should().Be(today);
        await streakService.DidNotReceive().RecalculateAsync(
            _userId, cancellationToken: Arg.Any<CancellationToken>());
        await gamificationService.DidNotReceive().ProcessHabitLogged(
            _userId, habit.Id, Arg.Any<CancellationToken>());
        await gamificationService.DidNotReceive().ProcessOnboardingChecklistAsync(
            _userId, OnboardingChecklistSignal.HabitLogged, Arg.Any<CancellationToken>());
        await challengeService.DidNotReceive().EvaluateOnHabitLoggedAsync(
            _userId, habit.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_BulkSkipReplay_ReturnsFirstResultWithoutWritingSecondLogs()
    {
        var today = new DateOnly(2026, 9, 24);
        var habits = new[]
        {
            Habit.Create(new HabitCreateParams(_userId, "First skip", FrequencyUnit.Week, 3,
                DueDate: today, IsFlexible: true)).Value,
            Habit.Create(new HabitCreateParams(_userId, "Second skip", FrequencyUnit.Week, 3,
                DueDate: today, IsFlexible: true)).Value
        };
        _dbContext.Habits.AddRange(habits);
        await _dbContext.SaveChangesAsync();

        var dateService = CreateUserDateService(today);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new BulkSkipHabitsCommandHandler(
            new GenericRepository<Habit>(_dbContext), new GenericRepository<HabitLog>(_dbContext),
            dateService, _unitOfWork, cache);
        var command = new BulkSkipHabitsCommand(_userId, habits.Select(h => new BulkSkipItem(h.Id)).ToList());
        var behavior = CreateBehavior<BulkSkipHabitsCommand, Result<BulkSkipResult>>();
        RequestHandlerDelegate<Result<BulkSkipResult>> next = ct =>
        {
            _handlerCalls++;
            return handler.Handle(command, ct);
        };

        var first = await behavior.Handle(command, next, CancellationToken.None);
        var replay = await behavior.Handle(command, next, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        first.Value.Results.Should().AllSatisfy(item => item.Status.Should().Be(BulkItemStatus.Success));
        replay.Value.Results.Should().BeEquivalentTo(first.Value.Results);
        _handlerCalls.Should().Be(1);
        foreach (var habit in habits)
            (await _dbContext.HabitLogs.AsNoTracking().CountAsync(log => log.HabitId == habit.Id && log.Value == 0))
                .Should().Be(1);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_BulkSkipLegacyRow_ReplaysWithoutAppendingAnotherSkipLog()
    {
        var today = new DateOnly(2026, 9, 24);
        var habit = Habit.Create(new HabitCreateParams(_userId, "Flexible skip", FrequencyUnit.Week, 3,
            DueDate: today, IsFlexible: true)).Value;
        _dbContext.Habits.Add(habit);
        await _dbContext.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new BulkSkipHabitsCommandHandler(
            new GenericRepository<Habit>(_dbContext), new GenericRepository<HabitLog>(_dbContext),
            CreateUserDateService(today), _unitOfWork, cache);
        var command = new BulkSkipHabitsCommand(_userId, [new BulkSkipItem(habit.Id, today)]);
        var context = new StubIdempotencyContext(true, _userId, "legacy-skip-key", trackOrdinals: true);
        var behavior = new IdempotencyBehavior<BulkSkipHabitsCommand, Result<BulkSkipResult>>(
            context, _store, _unitOfWork);
        RequestHandlerDelegate<Result<BulkSkipResult>> next = ct =>
        {
            _handlerCalls++;
            return handler.Handle(command, ct);
        };

        var first = await behavior.Handle(command, next, CancellationToken.None);
        var currentRow = await _dbContext.ProcessedRequests.SingleAsync();
        var legacyRow = ProcessedRequest.Create(_userId, "legacy-skip-key", typeof(BulkSkipHabitsCommand).FullName!, 0);
        legacyRow.SetResponseBody(currentRow.ResponseBody);
        _dbContext.ProcessedRequests.Remove(currentRow);
        _dbContext.ProcessedRequests.Add(legacyRow);
        await _dbContext.SaveChangesAsync();
        context.ResetOrdinals();

        var replay = await behavior.Handle(command, next, CancellationToken.None);

        replay.Value.Results.Should().BeEquivalentTo(first.Value.Results);
        _handlerCalls.Should().Be(1);
        (await _dbContext.HabitLogs.AsNoTracking().CountAsync(log => log.HabitId == habit.Id && log.Value == 0))
            .Should().Be(1);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_BulkSkipLegacyRowForDifferentHabit_DoesNotReplayIt()
    {
        var today = new DateOnly(2026, 9, 24);
        var habits = new[]
        {
            Habit.Create(new HabitCreateParams(_userId, "First flexible skip", FrequencyUnit.Week, 3,
                DueDate: today, IsFlexible: true)).Value,
            Habit.Create(new HabitCreateParams(_userId, "Second flexible skip", FrequencyUnit.Week, 3,
                DueDate: today, IsFlexible: true)).Value
        };
        _dbContext.Habits.AddRange(habits);
        await _dbContext.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new BulkSkipHabitsCommandHandler(
            new GenericRepository<Habit>(_dbContext), new GenericRepository<HabitLog>(_dbContext),
            CreateUserDateService(today), _unitOfWork, cache);
        var context = new StubIdempotencyContext(true, _userId, "different-legacy-skip", trackOrdinals: true);
        var behavior = new IdempotencyBehavior<BulkSkipHabitsCommand, Result<BulkSkipResult>>(
            context, _store, _unitOfWork);
        var firstCommand = new BulkSkipHabitsCommand(_userId, [new BulkSkipItem(habits[0].Id, today)]);
        (await behavior.Handle(firstCommand, ct => handler.Handle(firstCommand, ct), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        var currentRow = await _dbContext.ProcessedRequests.SingleAsync();
        var legacyRow = ProcessedRequest.Create(_userId, "different-legacy-skip",
            typeof(BulkSkipHabitsCommand).FullName!, 0);
        legacyRow.SetResponseBody(currentRow.ResponseBody);
        _dbContext.ProcessedRequests.Remove(currentRow);
        _dbContext.ProcessedRequests.Add(legacyRow);
        await _dbContext.SaveChangesAsync();
        context.ResetOrdinals();

        var secondCommand = new BulkSkipHabitsCommand(_userId, [new BulkSkipItem(habits[1].Id, today)]);
        var replay = await behavior.Handle(secondCommand, ct =>
        {
            _handlerCalls++;
            return handler.Handle(secondCommand, ct);
        }, CancellationToken.None);

        replay.Value.Results.Should().ContainSingle(item => item.HabitId == habits[1].Id);
        _handlerCalls.Should().Be(1);
        (await _dbContext.HabitLogs.AsNoTracking().CountAsync(log => log.Value == 0)).Should().Be(2);
    }

    [Fact]
    public async Task Handle_BulkLogReplay_ReturnsFirstResultWithoutWritingSecondLogs()
    {
        var today = new DateOnly(2026, 9, 24);
        var habits = new[]
        {
            Habit.Create(new HabitCreateParams(_userId, "First log", FrequencyUnit.Day, 1,
                DueDate: today)).Value,
            Habit.Create(new HabitCreateParams(_userId, "Second log", FrequencyUnit.Day, 1,
                DueDate: today)).Value
        };
        _dbContext.Habits.AddRange(habits);
        await _dbContext.SaveChangesAsync();

        var dateService = CreateUserDateService(today);
        var streakService = Substitute.For<IUserStreakService>();
        var gamificationService = Substitute.For<IGamificationService>();
        var goalService = Substitute.For<IGoalCompletionService>();
        goalService.SyncDerivedGoalsAsync(
                _userId, Arg.Any<IReadOnlyCollection<Guid>>(), today,
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<GoalCompletionUpdate>)Array.Empty<GoalCompletionUpdate>());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new BulkLogHabitsCommandHandler(
            new GenericRepository<Habit>(_dbContext), new GenericRepository<HabitLog>(_dbContext),
            new BulkLogServices(dateService, streakService, gamificationService),
            goalService, _unitOfWork, cache, NullLogger<BulkLogHabitsCommandHandler>.Instance);
        var command = new BulkLogHabitsCommand(_userId, habits.Select(h => new BulkLogItem(h.Id)).ToList());
        var behavior = CreateBehavior<BulkLogHabitsCommand, Result<BulkLogResult>>();
        RequestHandlerDelegate<Result<BulkLogResult>> next = ct =>
        {
            _handlerCalls++;
            return handler.Handle(command, ct);
        };

        var first = await behavior.Handle(command, next, CancellationToken.None);
        var replay = await behavior.Handle(command, next, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        first.Value.Results.Should().AllSatisfy(item => item.Status.Should().Be(BulkItemStatus.Success));
        first.Value.Results.Should().AllSatisfy(item => item.LogId.Should().NotBeNull());
        replay.Value.Results.Should().BeEquivalentTo(first.Value.Results);
        _handlerCalls.Should().Be(1);
        foreach (var habit in habits)
            (await _dbContext.HabitLogs.AsNoTracking().CountAsync(log => log.HabitId == habit.Id))
                .Should().Be(1);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_BulkSkipDistinctKeys_BothApply()
    {
        var today = new DateOnly(2026, 9, 24);
        var habits = new[]
        {
            Habit.Create(new HabitCreateParams(_userId, "First chunk", FrequencyUnit.Week, 3,
                DueDate: today, IsFlexible: true)).Value,
            Habit.Create(new HabitCreateParams(_userId, "Second chunk", FrequencyUnit.Week, 3,
                DueDate: today, IsFlexible: true)).Value
        };
        _dbContext.Habits.AddRange(habits);
        await _dbContext.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new BulkSkipHabitsCommandHandler(
            new GenericRepository<Habit>(_dbContext), new GenericRepository<HabitLog>(_dbContext),
            CreateUserDateService(today), _unitOfWork, cache);
        var firstCommand = new BulkSkipHabitsCommand(_userId, [new BulkSkipItem(habits[0].Id)]);
        var secondCommand = new BulkSkipHabitsCommand(_userId, [new BulkSkipItem(habits[1].Id)]);
        var firstBehavior = CreateBehavior<BulkSkipHabitsCommand, Result<BulkSkipResult>>(key: "chunk-1");
        var secondBehavior = CreateBehavior<BulkSkipHabitsCommand, Result<BulkSkipResult>>(key: "chunk-2");

        var first = await firstBehavior.Handle(firstCommand, ct =>
        {
            _handlerCalls++;
            return handler.Handle(firstCommand, ct);
        }, CancellationToken.None);
        var second = await secondBehavior.Handle(secondCommand, ct =>
        {
            _handlerCalls++;
            return handler.Handle(secondCommand, ct);
        }, CancellationToken.None);

        first.Value.Results.Should().ContainSingle(item => item.Status == BulkItemStatus.Success);
        second.Value.Results.Should().ContainSingle(item => item.Status == BulkItemStatus.Success);
        _handlerCalls.Should().Be(2);
        (await _dbContext.HabitLogs.AsNoTracking().CountAsync(log => log.Value == 0)).Should().Be(2);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(2);
    }

    private IUserDateService CreateUserDateService(DateOnly today)
    {
        var dateService = Substitute.For<IUserDateService>();
        dateService.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(today);
        dateService.GetUserWeekStartDayAsync(_userId, Arg.Any<CancellationToken>()).Returns(1);
        return dateService;
    }

    private IdempotencyBehavior<TRequest, TResponse> CreateBehavior<TRequest, TResponse>(
        bool hasKey = true, string key = "mutation-key-1")
        where TRequest : class =>
        new(new StubIdempotencyContext(hasKey, _userId, key), _store, _unitOfWork);

    private RequestHandlerDelegate<string> CreateTagHandler() =>
        async ct =>
        {
            _handlerCalls++;
            _dbContext.Tags.Add(Tag.Create(_userId, $"tag-{_handlerCalls}", "#ff0000").Value);
            await _unitOfWork.SaveChangesAsync(ct);
            return $"response-{_handlerCalls}";
        };

    private RequestHandlerDelegate<string> CountingHandler(string result) =>
        _ =>
        {
            _handlerCalls++;
            return Task.FromResult(result);
        };

    private sealed record FakeRequest : IRequest<string>, IIdempotentCommand;

    private sealed record NamedRequest(string Name) : IRequest<string>, IIdempotentCommand;

    private sealed record OtherRequest : IRequest<string>, IIdempotentCommand;

    private sealed record ResultRequest : IRequest<Result<string>>, IIdempotentCommand;

    private sealed record PlainResultRequest : IRequest<Result>, IIdempotentCommand;

    private sealed record UnmarkedRequest : IRequest<string>;

    private sealed class FakeUniqueViolationException : System.Data.Common.DbException
    {
        public override string SqlState => "23505";
    }

    private sealed class StubIdempotencyContext(bool hasKey, Guid userId, string key, bool trackOrdinals = false) : IIdempotencyContext
    {
        private readonly Dictionary<string, int> _nextOrdinalByType = new(StringComparer.Ordinal);

        public bool TryGetRequestKey(out Guid resolvedUserId, out string idempotencyKey)
        {
            resolvedUserId = userId;
            idempotencyKey = key;
            return hasKey;
        }

        public int NextRequestOrdinal(string requestType)
        {
            if (!trackOrdinals)
                return 0;

            _nextOrdinalByType.TryGetValue(requestType, out var ordinal);
            _nextOrdinalByType[requestType] = ordinal + 1;
            return ordinal;
        }

        public void ResetOrdinals() => _nextOrdinalByType.Clear();
    }

    private sealed class SqliteCompatOrbitDbContext(DbContextOptions<OrbitDbContext> options)
        : OrbitDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    var defaultSql = property.GetDefaultValueSql();
                    if (defaultSql is not null && defaultSql.Contains("::", StringComparison.Ordinal))
                        property.SetDefaultValueSql(null);
                }

                foreach (var index in entityType.GetIndexes())
                {
                    if (entityType.ClrType == typeof(HabitLog) && index.IsUnique)
                        index.SetFilter("CAST(\"Value\" AS REAL) > 0 AND NOT \"IsDeleted\"");
                    else
                        index.SetFilter(null);
                }
            }
        }
    }
}
