using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class AgentOperationExecutorReadArgumentTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static AgentOperationExecutor CreateExecutor(IMediator mediator, ILogger<AgentOperationExecutor> logger,
        IUserDateService dates)
    {
        IAiTool[] tools = [new GetHabitMetricsTool(mediator)];
        var registry = new AiToolRegistry(tools);
        var catalog = new AgentCatalogService(tools);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.EvaluateAsync(Arg.Any<AgentPolicyEvaluationContext>()).Returns(call => new AgentPolicyDecision(
            AgentPolicyDecisionStatus.Allowed, catalog.GetCapability(call.Arg<AgentPolicyEvaluationContext>().CapabilityId)!));
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var bridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);
        var blocks = new ReadBlockFactory(dates, Substitute.For<IReadQueryTokenService>(),
            Substitute.For<IGenericRepository<User>>());
        return new AgentOperationExecutor(catalog, policy, Substitute.For<IAgentAuditService>(), ownership, bridge,
            registry, Substitute.For<IPendingOperationChangePreviewer>(), Substitute.For<IPendingAgentOperationStore>(),
            Substitute.For<IUnitOfWork>(), logger, blocks);
    }

    private static IUserDateService Dates()
    {
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 11, 19));
        dates.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>()).Returns(1);
        return dates;
    }

    private static AgentExecuteOperationRequest Request(string json) => new(UserId, "get_habit_metrics",
        JsonSerializer.Deserialize<JsonElement>(json), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt);

    [Fact]
    public async Task GetHabitMetrics_FixedPeriodWithCount_ReturnsRuleMessageNotUnexpectedError()
    {
        var mediator = Substitute.For<IMediator>();
        var logger = Substitute.For<ILogger<AgentOperationExecutor>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var executor = CreateExecutor(mediator, logger, Dates());

        var response = await executor.ExecuteAsync(
            Request("""{"habit_id":"3fa85f64-5717-4562-b3fc-2c963f66afa6","relative_period":"last_7_days","period_count":7}"""),
            default);

        response.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        response.Operation.PolicyReason.Should().Be(
            "period_count requires a bounded positive count for last_n_days or last_n_weeks only.");
        response.Operation.ErrorCode.Should().Be(ToolFailureCodes.InvalidArguments);
        logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(call => (LogLevel)call.GetArguments()[0]!).Should().NotContain(LogLevel.Error);
        await mediator.DidNotReceiveWithAnyArgs().Send<object>(default!, default);
    }

    [Fact]
    public async Task GetHabitMetrics_UnexpectedFailureWhileResolving_StillAnswersUnexpectedError()
    {
        var dates = Dates();
        dates.GetUserWeekStartDayAsync(UserId, Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("boom"));
        var executor = CreateExecutor(Substitute.For<IMediator>(), NullLogger<AgentOperationExecutor>.Instance, dates);

        var response = await executor.ExecuteAsync(
            Request("""{"habit_id":"3fa85f64-5717-4562-b3fc-2c963f66afa6","relative_period":"last_7_days"}"""), default);

        response.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        response.Operation.PolicyReason.Should().Be("unexpected_error");
    }
}
