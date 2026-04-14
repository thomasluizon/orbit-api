using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Domain.Common;
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
    public async Task GetCapabilitiesMetadata_ReturnsForbidWhenPolicyDenies()
    {
        _policyEvaluator.Evaluate(Arg.Any<AgentPolicyEvaluationContext>())
            .Returns(new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, null));

        var result = await _controller.GetCapabilitiesMetadata(CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        await _auditService.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Fact]
    public async Task GetCapabilitiesMetadata_ReturnsCatalogAndAudits()
    {
        IReadOnlyList<AgentCapability> capabilities =
        [
            new AgentCapability(
                AgentCapabilityIds.CatalogCapabilitiesRead,
                "Capabilities",
                "Read capability catalog",
                "catalog",
                AgentScopes.CatalogRead,
                AgentRiskClass.Low,
                false,
                false,
                AgentConfirmationRequirement.None)
        ];
        _policyEvaluator.Evaluate(Arg.Any<AgentPolicyEvaluationContext>())
            .Returns(new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, null));
        _catalogService.GetCapabilities().Returns(capabilities);

        var result = await _controller.GetCapabilitiesMetadata(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(capabilities);
        await _auditService.Received(1).RecordAsync(
            Arg.Is<AgentAuditEntry>(entry =>
                entry.UserId == UserId &&
                entry.SourceName == nameof(AiController.GetCapabilitiesMetadata) &&
                entry.PolicyDecision == AgentPolicyDecisionStatus.Allowed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOperationsMetadata_ReturnsCatalogAndAudits()
    {
        IReadOnlyList<AgentOperation> operations =
        [
            new AgentOperation(
                "list_habits",
                "List habits",
                "Read habits",
                AgentCapabilityIds.HabitsRead,
                AgentRiskClass.Low,
                AgentConfirmationRequirement.None,
                false,
                true,
                Parse("{}"),
                Parse("{}"))
        ];
        _policyEvaluator.Evaluate(Arg.Any<AgentPolicyEvaluationContext>())
            .Returns(new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, null));
        _catalogService.GetOperations().Returns(operations);

        var result = await _controller.GetOperationsMetadata(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(operations);
        await _auditService.Received(1).RecordAsync(
            Arg.Is<AgentAuditEntry>(entry => entry.SourceName == nameof(AiController.GetOperationsMetadata)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUserDataCatalog_ReturnsCatalogAndAudits()
    {
        IReadOnlyList<UserDataCatalogEntry> dataCatalog =
        [
            new UserDataCatalogEntry("profile", "Profile", "Profile data", "medium", "keep", true, true, [])
        ];
        _policyEvaluator.Evaluate(Arg.Any<AgentPolicyEvaluationContext>())
            .Returns(new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, null));
        _catalogService.GetUserDataCatalog().Returns(dataCatalog);

        var result = await _controller.GetUserDataCatalog(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(dataCatalog);
        await _auditService.Received(1).RecordAsync(
            Arg.Is<AgentAuditEntry>(entry => entry.SourceName == nameof(AiController.GetUserDataCatalog)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAppSurfaces_ReturnsCatalogAndAudits()
    {
        IReadOnlyList<AppSurface> surfaces =
        [
            new AppSurface("today", "Today", "Today screen", [], [], [], [])
        ];
        _policyEvaluator.Evaluate(Arg.Any<AgentPolicyEvaluationContext>())
            .Returns(new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, null));
        _catalogService.GetSurfaces().Returns(surfaces);

        var result = await _controller.GetAppSurfaces(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(surfaces);
        await _auditService.Received(1).RecordAsync(
            Arg.Is<AgentAuditEntry>(entry => entry.SourceName == nameof(AiController.GetAppSurfaces)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmPendingOperation_ForApiKeyUser_ReturnsForbid()
    {
        SetUser(isApiKey: true);

        var result = await _controller.ConfirmPendingOperation(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task ConfirmPendingOperation_NotFound_ReturnsNotFoundAndAudits()
    {
        var pendingOperationId = Guid.NewGuid();
        _pendingOperationStore.Confirm(UserId, pendingOperationId).Returns((PendingAgentOperationConfirmation?)null);

        var result = await _controller.ConfirmPendingOperation(pendingOperationId, CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
        await _auditService.Received(1).RecordAsync(
            Arg.Is<AgentAuditEntry>(entry =>
                entry.TargetId == pendingOperationId.ToString() &&
                entry.OutcomeStatus == AgentOperationStatus.Failed &&
                entry.Error == "pending_operation_not_found"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmPendingOperation_ReturnsConfirmationToken()
    {
        var pendingOperationId = Guid.NewGuid();
        var confirmation = new PendingAgentOperationConfirmation(
            pendingOperationId,
            "agc_token",
            DateTime.UtcNow.AddMinutes(5));
        _pendingOperationStore.Confirm(UserId, pendingOperationId).Returns(confirmation);

        var result = await _controller.ConfirmPendingOperation(pendingOperationId, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<AiController.ConfirmPendingOperationResponse>().Subject;
        response.PendingOperationId.Should().Be(pendingOperationId);
        response.ConfirmationToken.Should().Be("agc_token");
    }

    [Fact]
    public async Task MarkPendingOperationStepUp_ForApiKeyUser_ReturnsForbid()
    {
        SetUser(isApiKey: true);

        var result = await _controller.MarkPendingOperationStepUp(Guid.NewGuid(), new AiController.StepUpChallengeRequest(), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task MarkPendingOperationStepUp_ReturnsBadRequestOnFailure()
    {
        var pendingOperationId = Guid.NewGuid();
        _stepUpService.IssueChallengeAsync(UserId, pendingOperationId, "pt-BR", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AgentStepUpChallenge>("step_up_failed"));

        var result = await _controller.MarkPendingOperationStepUp(
            pendingOperationId,
            new AiController.StepUpChallengeRequest("pt-BR"),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        await _auditService.Received(1).RecordAsync(
            Arg.Is<AgentAuditEntry>(entry => entry.Error == "step_up_failed"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkPendingOperationStepUp_ReturnsChallenge()
    {
        var pendingOperationId = Guid.NewGuid();
        var challenge = new AgentStepUpChallenge(Guid.NewGuid(), pendingOperationId, DateTime.UtcNow.AddMinutes(10));
        _stepUpService.IssueChallengeAsync(UserId, pendingOperationId, "en", Arg.Any<CancellationToken>())
            .Returns(Result.Success(challenge));

        var result = await _controller.MarkPendingOperationStepUp(
            pendingOperationId,
            new AiController.StepUpChallengeRequest(),
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(challenge);
    }

    [Fact]
    public async Task VerifyPendingOperationStepUp_ForApiKeyUser_ReturnsForbid()
    {
        SetUser(isApiKey: true);

        var result = await _controller.VerifyPendingOperationStepUp(
            Guid.NewGuid(),
            new AiController.VerifyStepUpRequest(Guid.NewGuid(), "123456"),
            CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task VerifyPendingOperationStepUp_ReturnsBadRequestOnFailure()
    {
        var pendingOperationId = Guid.NewGuid();
        var challengeId = Guid.NewGuid();
        _stepUpService.VerifyChallengeAsync(UserId, pendingOperationId, challengeId, "123456", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<PendingAgentOperation>("verify_failed"));

        var result = await _controller.VerifyPendingOperationStepUp(
            pendingOperationId,
            new AiController.VerifyStepUpRequest(challengeId, "123456"),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        await _auditService.Received(1).RecordAsync(
            Arg.Is<AgentAuditEntry>(entry => entry.Error == "verify_failed"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyPendingOperationStepUp_ReturnsPendingOperation()
    {
        var pendingOperationId = Guid.NewGuid();
        var challengeId = Guid.NewGuid();
        var pendingOperation = new PendingAgentOperation(
            pendingOperationId,
            AgentCapabilityIds.HabitsDelete,
            "Delete habit",
            "Delete a habit",
            AgentRiskClass.High,
            AgentConfirmationRequirement.StepUp,
            DateTime.UtcNow.AddMinutes(10));
        _stepUpService.VerifyChallengeAsync(UserId, pendingOperationId, challengeId, "123456", Arg.Any<CancellationToken>())
            .Returns(Result.Success(pendingOperation));

        var result = await _controller.VerifyPendingOperationStepUp(
            pendingOperationId,
            new AiController.VerifyStepUpRequest(challengeId, "123456"),
            CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().Be(pendingOperation);
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

    [Fact]
    public async Task ExecutePendingOperation_ForApiKeyUser_ReturnsForbid()
    {
        SetUser(isApiKey: true);

        var result = await _controller.ExecutePendingOperation(
            Guid.NewGuid(),
            new AiController.ExecutePendingOperationRequest("agc_token"),
            CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    private void SetUser(bool isApiKey = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, UserId.ToString()) };
        if (isApiKey)
            claims.Add(new Claim("auth_method", "api_key"));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
