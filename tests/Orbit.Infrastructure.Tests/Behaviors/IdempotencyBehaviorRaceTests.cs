using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Behaviors;

public class IdempotencyBehaviorRaceTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string Key = "mutation-key-1";

    private static readonly JsonSerializerOptions LedgerSerializerOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new ResultJsonConverterFactory() } };

    [Fact]
    public async Task Handle_ConcurrentDuplicateLosesUniqueRace_ReplaysWinnerResponse()
    {
        var store = Substitute.For<IIdempotencyStore>();
        store.FindResponseBodyAsync(UserId, Key, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null), Task.FromResult<string?>("\"winner-response\""));
        store.Reserve(UserId, Key, Arg.Any<string>()).Returns(Substitute.For<IIdempotencyReservation>());

        var unitOfWork = BuildUnitOfWorkThatThrowsUniqueViolationOnSave();
        var behavior = new IdempotencyBehavior<FakeRequest, string>(BuildContextWithKey(), store, unitOfWork);

        var handlerCalls = 0;
        RequestHandlerDelegate<string> next = _ =>
        {
            handlerCalls++;
            return Task.FromResult("loser-response");
        };

        var result = await behavior.Handle(new FakeRequest(), next, CancellationToken.None);

        result.Should().Be("winner-response");
        handlerCalls.Should().Be(0);
    }

    [Fact]
    public async Task Handle_UniqueViolationWithNoStoredResponse_Rethrows()
    {
        var store = Substitute.For<IIdempotencyStore>();
        store.FindResponseBodyAsync(UserId, Key, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        store.Reserve(UserId, Key, Arg.Any<string>()).Returns(Substitute.For<IIdempotencyReservation>());

        var unitOfWork = BuildUnitOfWorkThatThrowsUniqueViolationOnSave();
        var behavior = new IdempotencyBehavior<FakeRequest, string>(BuildContextWithKey(), store, unitOfWork);
        RequestHandlerDelegate<string> next = _ => Task.FromResult("value");

        var act = () => behavior.Handle(new FakeRequest(), next, CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Handle_ConcurrentDuplicateBatchCommand_ReplaysWinnersFullBatchResponse()
    {
        var winnerBatch = Result.Success(new FakeBatchResponse(new List<FakeBatchItem>
        {
            new(0, "Success", Guid.NewGuid()),
            new(1, "Failed", Guid.NewGuid()),
            new(2, "Success", Guid.NewGuid())
        }));
        var winnerJson = JsonSerializer.Serialize(winnerBatch, LedgerSerializerOptions);

        var store = Substitute.For<IIdempotencyStore>();
        store.FindResponseBodyAsync(UserId, Key, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null), Task.FromResult<string?>(winnerJson));
        store.Reserve(UserId, Key, Arg.Any<string>()).Returns(Substitute.For<IIdempotencyReservation>());

        var unitOfWork = BuildUnitOfWorkThatThrowsUniqueViolationOnSave();
        var behavior = new IdempotencyBehavior<FakeBatchRequest, Result<FakeBatchResponse>>(
            BuildContextWithKey(), store, unitOfWork);

        var handlerCalls = 0;
        RequestHandlerDelegate<Result<FakeBatchResponse>> next = _ =>
        {
            handlerCalls++;
            return Task.FromResult(Result.Success(new FakeBatchResponse(new List<FakeBatchItem>
            {
                new(0, "Failed", Guid.NewGuid())
            })));
        };

        var result = await behavior.Handle(new FakeBatchRequest(), next, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().HaveCount(3);
        result.Value.Results.Should().BeEquivalentTo(
            winnerBatch.Value.Results, options => options.WithStrictOrdering());
        handlerCalls.Should().Be(0);
    }

    private static IIdempotencyContext BuildContextWithKey()
    {
        var context = Substitute.For<IIdempotencyContext>();
        context.TryGetRequestKey(out Arg.Any<Guid>(), out Arg.Any<string>())
            .Returns(call =>
            {
                call[0] = UserId;
                call[1] = Key;
                return true;
            });
        return context;
    }

    private static IUnitOfWork BuildUnitOfWorkThatThrowsUniqueViolationOnSave()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>().Invoke(CancellationToken.None));
        unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException(
                "duplicate key value violates unique constraint",
                new PostgresException("duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation)));
        return unitOfWork;
    }

    private sealed record FakeRequest : IRequest<string>, IIdempotentCommand;

    private sealed record FakeBatchRequest : IRequest<Result<FakeBatchResponse>>, IIdempotentCommand;

    private sealed record FakeBatchResponse(IReadOnlyList<FakeBatchItem> Results);

    private sealed record FakeBatchItem(int Index, string Status, Guid HabitId);
}
