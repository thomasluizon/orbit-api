using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Behaviors;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Behaviors;

public class ConcurrencyRetryBehaviorTests
{
    private sealed record RetryableRequest : IRequest<string>, IConcurrencyRetryable;

    private sealed record PlainRequest : IRequest<string>;

    [Fact]
    public async Task MarkedRequest_ConflictThenSuccess_RetriesAndResetsTracking()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var behavior = new ConcurrencyRetryBehavior<RetryableRequest, string>(unitOfWork);
        var attempts = 0;
        RequestHandlerDelegate<string> next = _ =>
        {
            attempts++;
            return attempts == 1
                ? throw new DbUpdateConcurrencyException("simulated stale token")
                : Task.FromResult("ok");
        };

        var result = await behavior.Handle(new RetryableRequest(), next, CancellationToken.None);

        result.Should().Be("ok");
        attempts.Should().Be(2);
        unitOfWork.Received(1).ResetTracking();
    }

    [Fact]
    public async Task MarkedRequest_PersistentConflict_PropagatesAfterThreeAttempts()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var behavior = new ConcurrencyRetryBehavior<RetryableRequest, string>(unitOfWork);
        var attempts = 0;
        RequestHandlerDelegate<string> next = _ =>
        {
            attempts++;
            throw new DbUpdateConcurrencyException("simulated stale token");
        };

        var act = async () => await behavior.Handle(new RetryableRequest(), next, CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        attempts.Should().Be(3);
        unitOfWork.Received(2).ResetTracking();
    }

    [Fact]
    public async Task UnmarkedRequest_Conflict_DoesNotRetry()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var behavior = new ConcurrencyRetryBehavior<PlainRequest, string>(unitOfWork);
        var attempts = 0;
        RequestHandlerDelegate<string> next = _ =>
        {
            attempts++;
            throw new DbUpdateConcurrencyException("simulated stale token");
        };

        var act = async () => await behavior.Handle(new PlainRequest(), next, CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        attempts.Should().Be(1);
        unitOfWork.DidNotReceive().ResetTracking();
    }
}
