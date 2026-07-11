using FluentAssertions;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
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

        _unitOfWork = new UnitOfWork(_dbContext);
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
        var behavior = CreateBehavior(hasKey: true);

        var response = await behavior.Handle(new FakeRequest(), CreateTagHandler(), CancellationToken.None);

        response.Should().Be("response-1");
        _handlerCalls.Should().Be(1);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(1);
        (await _dbContext.Tags.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_ReplayedKey_ReturnsStoredResponseWithoutReExecuting()
    {
        var behavior = CreateBehavior(hasKey: true);
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
    public async Task Handle_NoIdempotencyKey_BypassesLedgerAndRunsHandler()
    {
        var behavior = CreateBehavior(hasKey: false);

        var response = await behavior.Handle(new FakeRequest(), CreateTagHandler(), CancellationToken.None);

        response.Should().Be("response-1");
        _handlerCalls.Should().Be(1);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_HandlerThrows_RollsBackReservationAndMutationTogether()
    {
        var behavior = CreateBehavior(hasKey: true);
        RequestHandlerDelegate<string> throwingHandler = async _ =>
        {
            _dbContext.Tags.Add(Tag.Create(_userId, "doomed-tag", "#ff0000").Value);
            await _unitOfWork.SaveChangesAsync();
            throw new InvalidOperationException("handler failed after a partial write");
        };

        var act = () => behavior.Handle(new FakeRequest(), throwingHandler, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _dbContext.Tags.CountAsync()).Should().Be(0);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_UnitResponse_RoundTripsThroughLedgerOnReplay()
    {
        var behavior = new IdempotencyBehavior<FakeRequest, Unit>(
            new StubIdempotencyContext(true, _userId, "unit-key"), _store, _unitOfWork);
        RequestHandlerDelegate<Unit> handler = _ => Task.FromResult(Unit.Value);

        var first = await behavior.Handle(new FakeRequest(), handler, CancellationToken.None);
        var replay = await behavior.Handle(new FakeRequest(), handler, CancellationToken.None);

        first.Should().Be(Unit.Value);
        replay.Should().Be(Unit.Value);
        (await _dbContext.ProcessedRequests.CountAsync()).Should().Be(1);
    }

    private IdempotencyBehavior<FakeRequest, string> CreateBehavior(bool hasKey) =>
        new(new StubIdempotencyContext(hasKey, _userId, "mutation-key-1"), _store, _unitOfWork);

    private RequestHandlerDelegate<string> CreateTagHandler() =>
        async _ =>
        {
            _handlerCalls++;
            _dbContext.Tags.Add(Tag.Create(_userId, $"tag-{_handlerCalls}", "#ff0000").Value);
            await _unitOfWork.SaveChangesAsync();
            return $"response-{_handlerCalls}";
        };

    private sealed record FakeRequest : IRequest<string>;

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
