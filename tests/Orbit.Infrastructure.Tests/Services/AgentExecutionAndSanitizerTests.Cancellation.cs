using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public partial class AgentExecutionAndSanitizerTests
{
    [Fact]
    public async Task AgentOperationExecutor_CanceledReadPropagatesAndAuditsWithoutErrorLogging()
    {
        using var caller = new CancellationTokenSource();
        var audit = Substitute.For<IAgentAuditService>();
        var logger = Substitute.For<ILogger<AgentOperationExecutor>>();
        logger.IsEnabled(LogLevel.Error).Returns(true);
        var failure = new OperationCanceledException(caller.Token);
        var executor = CreateReadExecutor(token =>
        {
            caller.Cancel();
            throw failure;
        }, audit, logger);

        var execute = () => executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId, "get_retrospective", Parse("{}"), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), caller.Token);

        (await execute.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(failure);
        await audit.Received(1).RecordAsync(Arg.Is<AgentAuditEntry>(entry =>
            entry.OutcomeStatus == AgentOperationStatus.Failed && entry.Error == "caller_canceled"),
            Arg.Is<CancellationToken>(token => token.CanBeCanceled && !token.IsCancellationRequested));
        logger.DidNotReceive().Log(LogLevel.Error, Arg.Any<EventId>(), Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(), Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Fact]
    public async Task AgentOperationExecutor_CompletedReadAuditsAfterCallerDisconnects()
    {
        using var caller = new CancellationTokenSource();
        var audit = Substitute.For<IAgentAuditService>();
        audit.RecordAsync(Arg.Any<AgentAuditEntry>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        var executor = CreateReadExecutor(_ =>
        {
            caller.Cancel();
            return Task.FromResult(new ToolResult(true));
        }, audit, Substitute.For<ILogger<AgentOperationExecutor>>());

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId, "get_retrospective", Parse("{}"), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt), caller.Token);

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        await audit.Received(1).RecordAsync(Arg.Is<AgentAuditEntry>(entry =>
            entry.OutcomeStatus == AgentOperationStatus.Succeeded),
            Arg.Is<CancellationToken>(token => token.CanBeCanceled && !token.IsCancellationRequested));
    }

    [Fact]
    public async Task AgentOperationExecutor_UnrequestedCancellationRemainsAnUnexpectedFailure()
    {
        var logger = Substitute.For<ILogger<AgentOperationExecutor>>();
        logger.IsEnabled(LogLevel.Error).Returns(true);
        var executor = CreateReadExecutor(_ => throw new OperationCanceledException(),
            Substitute.For<IAgentAuditService>(), logger);

        var response = await executor.ExecuteAsync(new AgentExecuteOperationRequest(
            UserId, "get_retrospective", Parse("{}"), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt));

        response.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        logger.Received(1).Log(LogLevel.Error, Arg.Is<EventId>(id => id.Id == 2), Arg.Any<Arg.AnyType>(),
            Arg.Is<Exception>(ex => ex is RedactedOperationException), Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    private static AgentOperationExecutor CreateReadExecutor(Func<CancellationToken, Task<ToolResult>> execute,
        IAgentAuditService audit, ILogger<AgentOperationExecutor> logger)
    {
        var catalog = Substitute.For<IAgentCatalogService>();
        var capability = CreateCapability(AgentCapabilityIds.HabitsRead, AgentScopes.ReadHabits,
            AgentRiskClass.Low, AgentConfirmationRequirement.None, isMutation: false);
        var operation = CreateOperation("get_retrospective", capability.Id, isMutation: false,
            isAgentExecutable: true, AgentConfirmationRequirement.None, AgentRiskClass.Low);
        catalog.GetOperation(operation.Id).Returns(operation);
        catalog.GetCapability(capability.Id).Returns(capability);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>())
            .Returns(new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, capability));
        return CreateExecutor(catalog, policyEvaluator: policy, auditService: audit, logger: logger,
            toolRegistry: new AiToolRegistry([new StubTool(operation.Id, (_, _, token) => execute(token), isReadOnly: true)]));
    }
}
