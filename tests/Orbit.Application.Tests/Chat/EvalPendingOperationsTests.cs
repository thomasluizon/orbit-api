using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public class EvalPendingOperationsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validators_RequireTheAuthenticatedAccount(bool valid)
    {
        var userId = valid ? Guid.NewGuid() : Guid.Empty;
        new ClearEvalPendingOperationsCommandValidator().Validate(new ClearEvalPendingOperationsCommand(userId)).IsValid.Should().Be(valid);
        new GetEvalPendingOperationsQueryValidator().Validate(new GetEvalPendingOperationsQuery(userId)).IsValid.Should().Be(valid);
    }

    [Fact]
    public async Task Clear_CancelsOnlyTheOwnersUnconsumedOperationsAndSavesBeforeReturning()
    {
        var owner = Guid.NewGuid();
        var operation = Create(owner);
        var foreign = Create(Guid.NewGuid());
        var consumed = Create(owner);
        consumed.MarkConsumed();
        var operations = Substitute.For<IGenericRepository<PendingAgentOperationState>>();
        operations.FindTrackedAsync(Arg.Any<Expression<Func<PendingAgentOperationState, bool>>>(), default)
            .Returns(call => new[] { operation, foreign, consumed }.Where(call
                .Arg<Expression<Func<PendingAgentOperationState, bool>>>().Compile()).ToArray());
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).Returns(_ =>
        {
            operation.ConsumedAtUtc.Should().NotBeNull();
            foreign.ConsumedAtUtc.Should().BeNull();
            return 1;
        });

        var result = await new ClearEvalPendingOperationsCommandHandler(operations, unitOfWork)
            .Handle(new(owner), default);

        result.Value.CancelledCount.Should().Be(1);
        await unitOfWork.Received(1).SaveChangesAsync(default);
    }

    [Fact]
    public async Task Clear_SaveFailureDoesNotReportSuccess()
    {
        var operations = Substitute.For<IGenericRepository<PendingAgentOperationState>>();
        operations.FindTrackedAsync(Arg.Any<Expression<Func<PendingAgentOperationState, bool>>>(), default)
            .Returns(Array.Empty<PendingAgentOperationState>());
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).Returns(Task.FromException<int>(new InvalidOperationException("save failed")));
        var handler = new ClearEvalPendingOperationsCommandHandler(operations, unitOfWork);
        await FluentActions.Awaiting(() => handler.Handle(new(Guid.NewGuid()), default))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Readback_CountsOnlyTheOwnersUnconsumedRows()
    {
        var owner = Guid.NewGuid();
        var pending = Create(owner);
        var consumed = Create(owner);
        consumed.MarkConsumed();
        var operations = Substitute.For<IGenericRepository<PendingAgentOperationState>>();
        operations.CountAsync(Arg.Any<Expression<Func<PendingAgentOperationState, bool>>>(), default)
            .Returns(call => new[] { pending, consumed, Create(Guid.NewGuid()) }
                .Count(call.Arg<Expression<Func<PendingAgentOperationState, bool>>>().Compile()));
        var result = await new GetEvalPendingOperationsQueryHandler(operations).Handle(new(owner), default);
        result.Value.PendingCount.Should().Be(1);
    }

    private static PendingAgentOperationState Create(Guid owner) => PendingAgentOperationState.Create(new()
    {
        UserId = owner,
        Capability = new AgentCapability("test", "Test", "Test", "test", "test", AgentRiskClass.Low,
            true, false, AgentConfirmationRequirement.FreshConfirmation),
        OperationId = "test",
        ArgumentsJson = "{}",
        Summary = "Preview",
        OperationFingerprint = "fingerprint",
        Surface = AgentExecutionSurface.Chat,
        ExpiresAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime.AddMinutes(-1)
    });
}
