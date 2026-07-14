using FluentAssertions;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
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

    private IdempotencyBehavior<TRequest, TResponse> CreateBehavior<TRequest, TResponse>(bool hasKey = true)
        where TRequest : class =>
        new(new StubIdempotencyContext(hasKey, _userId, "mutation-key-1"), _store, _unitOfWork);

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
