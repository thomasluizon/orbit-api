using FluentAssertions;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Behaviors;
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
                    $"INSERT INTO \"HabitLogs\" (\"Id\", \"HabitId\", \"Date\", \"Value\", \"CreatedAtUtc\", \"UpdatedAtUtc\", \"IsDeleted\") VALUES ({winnerId}, {habit.Id}, {today}, {1m}, {now}, {now}, {false})", ct);
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

    private sealed record OtherRequest : IRequest<string>, IIdempotentCommand;

    private sealed record ResultRequest : IRequest<Result<string>>, IIdempotentCommand;

    private sealed record PlainResultRequest : IRequest<Result>, IIdempotentCommand;

    private sealed record UnmarkedRequest : IRequest<string>;

    private sealed class FakeUniqueViolationException : System.Data.Common.DbException
    {
        public override string SqlState => "23505";
    }

    private sealed class StubIdempotencyContext(bool hasKey, Guid userId, string key) : IIdempotencyContext
    {
        public bool TryGetRequestKey(out Guid resolvedUserId, out string idempotencyKey)
        {
            resolvedUserId = userId;
            idempotencyKey = key;
            return hasKey;
        }
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
                    index.SetFilter(null);
            }
        }
    }
}
