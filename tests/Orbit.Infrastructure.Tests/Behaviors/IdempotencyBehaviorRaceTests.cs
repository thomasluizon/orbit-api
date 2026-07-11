using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Behaviors;

public class IdempotencyBehaviorRaceTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string Key = "mutation-key-1";

    [Fact]
    public async Task Handle_ConcurrentDuplicateLosesUniqueRace_ReplaysWinnerResponse()
    {
        var store = Substitute.For<IIdempotencyStore>();
        store.FindResponseBodyAsync(UserId, Key, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null), Task.FromResult<string?>("\"winner-response\""));
        store.Reserve(UserId, Key).Returns(Substitute.For<IIdempotencyReservation>());

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
        handlerCalls.Should().Be(1);
    }

    [Fact]
    public async Task Handle_UniqueViolationWithNoStoredResponse_Rethrows()
    {
        var store = Substitute.For<IIdempotencyStore>();
        store.FindResponseBodyAsync(UserId, Key, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        store.Reserve(UserId, Key).Returns(Substitute.For<IIdempotencyReservation>());

        var unitOfWork = BuildUnitOfWorkThatThrowsUniqueViolationOnSave();
        var behavior = new IdempotencyBehavior<FakeRequest, string>(BuildContextWithKey(), store, unitOfWork);
        RequestHandlerDelegate<string> next = _ => Task.FromResult("value");

        var act = () => behavior.Handle(new FakeRequest(), next, CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
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

    private sealed record FakeRequest : IRequest<string>;
}
