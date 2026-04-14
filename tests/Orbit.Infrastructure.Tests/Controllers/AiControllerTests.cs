using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public class AiControllerTests
{
    private readonly IAgentCatalogService _catalogService = Substitute.For<IAgentCatalogService>();
    private readonly IAgentPolicyEvaluator _policyEvaluator = Substitute.For<IAgentPolicyEvaluator>();
    private readonly IPendingAgentOperationStore _pendingOperationStore = Substitute.For<IPendingAgentOperationStore>();
    private readonly IAgentStepUpService _stepUpService = Substitute.For<IAgentStepUpService>();
    private readonly IAgentAuditService _auditService = Substitute.For<IAgentAuditService>();
    private readonly IAgentOperationExecutor _operationExecutor = Substitute.For<IAgentOperationExecutor>();
    private readonly AiController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public AiControllerTests()
    {
        _controller = new AiController(
            _catalogService,
            _policyEvaluator,
            _pendingOperationStore,
            _stepUpService,
            _auditService,
            _operationExecutor);

        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    [Fact]
    public async Task ExecutePendingOperation_NotFound_ReturnsNotFound()
    {
        _pendingOperationStore.GetExecution(UserId, Arg.Any<Guid>()).Returns((PendingAgentOperationExecution?)null);

        var result = await _controller.ExecutePendingOperation(
            Guid.NewGuid(),
            new AiController.ExecutePendingOperationRequest("agc_token"),
            CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task ExecutePendingOperation_UsesStoredPayload()
    {
        var pendingOperationId = Guid.NewGuid();
        using var argsDocument = JsonDocument.Parse("{\"habit_id\":\"habit-123\"}");

        _pendingOperationStore.GetExecution(UserId, pendingOperationId)
            .Returns(new PendingAgentOperationExecution(
                pendingOperationId,
                AgentCapabilityIds.HabitsDelete,
                "delete_habit",
                argsDocument.RootElement.Clone(),
                AgentExecutionSurface.Chat,
                AgentConfirmationRequirement.FreshConfirmation));

        var response = new AgentExecuteOperationResponse(new AgentOperationResult(
            "delete_habit",
            "delete_habit",
            AgentRiskClass.Destructive,
            AgentConfirmationRequirement.FreshConfirmation,
            AgentOperationStatus.Succeeded,
            Summary: "Delete habit requested via Chat",
            TargetId: "habit-123",
            TargetName: "Exercise"));

        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(response);

        var result = await _controller.ExecutePendingOperation(
            pendingOperationId,
            new AiController.ExecutePendingOperationRequest("agc_token"),
            CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be(response);

        await _operationExecutor.Received(1).ExecuteAsync(
            Arg.Is<AgentExecuteOperationRequest>(request =>
                request.UserId == UserId &&
                request.OperationId == "delete_habit" &&
                request.Surface == AgentExecutionSurface.Chat &&
                request.ConfirmationToken == "agc_token" &&
                request.Arguments.ValueKind == JsonValueKind.Object &&
                request.Arguments.GetProperty("habit_id").GetString() == "habit-123"),
            Arg.Any<CancellationToken>());
    }
}
