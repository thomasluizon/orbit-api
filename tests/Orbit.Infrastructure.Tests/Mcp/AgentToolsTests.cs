using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Api.Mcp.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class AgentToolsTests
{
    private readonly IAgentCatalogService _catalogService = Substitute.For<IAgentCatalogService>();
    private readonly IAgentOperationExecutor _operationExecutor = Substitute.For<IAgentOperationExecutor>();
    private readonly IPendingAgentOperationStore _pendingOperationStore = Substitute.For<IPendingAgentOperationStore>();
    private readonly IAgentStepUpService _stepUpService = Substitute.For<IAgentStepUpService>();
    private readonly AgentTools _tools;
    private static readonly Guid UserId = Guid.NewGuid();

    public AgentToolsTests()
    {
        _tools = new AgentTools(_catalogService, _operationExecutor, _pendingOperationStore, _stepUpService);
    }

    [Fact]
    public void ListMethods_ReturnCatalogValues()
    {
        var capability = new AgentCapability(
            AgentCapabilityIds.HabitsRead,
            "Habits",
            "Read habits",
            "habits",
            AgentScopes.ReadHabits,
            AgentRiskClass.Low,
            false,
            false,
            AgentConfirmationRequirement.None);
        var operation = new AgentOperation(
            "list_habits",
            "List habits",
            "Read habits",
            AgentCapabilityIds.HabitsRead,
            AgentRiskClass.Low,
            AgentConfirmationRequirement.None,
            false,
            true,
            Parse("{}"),
            Parse("{}"));
        var surface = new AppSurface("today", "Today", "Today view", [], [], [], []);
        var dataCatalog = new UserDataCatalogEntry("habits", "Habits", "Habit data", "low", "keep", true, true, []);

        _catalogService.GetCapabilities().Returns([capability]);
        _catalogService.GetOperations().Returns([operation]);
        _catalogService.GetSurfaces().Returns([surface]);
        _catalogService.GetUserDataCatalog().Returns([dataCatalog]);

        _tools.ListCapabilities().Should().ContainSingle().Which.Should().Be(capability);
        _tools.ListOperations().Should().ContainSingle().Which.Should().Be(operation);
        _tools.ListAppSurfaces().Should().ContainSingle().Which.Should().Be(surface);
        _tools.ListUserDataCatalog().Should().ContainSingle().Which.Should().Be(dataCatalog);
    }

    [Fact]
    public async Task ExecuteAgentOperation_UsesDefaultArgumentsAndUserContext()
    {
        var user = CreateUser();
        var response = new AgentExecuteOperationResponse(new AgentOperationResult(
            "list_habits",
            "list_habits",
            AgentRiskClass.Low,
            AgentConfirmationRequirement.None,
            AgentOperationStatus.Succeeded));
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(response);

        var result = await _tools.ExecuteAgentOperation(user, "list_habits", null, "token-123", CancellationToken.None);

        result.Should().Be(response);
        await _operationExecutor.Received(1).ExecuteAsync(
            Arg.Is<AgentExecuteOperationRequest>(request =>
                request.UserId == UserId &&
                request.OperationId == "list_habits" &&
                request.Surface == AgentExecutionSurface.Mcp &&
                request.AuthMethod == AgentAuthMethod.Jwt &&
                request.ConfirmationToken == "token-123" &&
                request.Arguments.ValueKind == JsonValueKind.Object &&
                !request.Arguments.EnumerateObject().Any()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ConfirmAgentOperation_ThrowsForApiKeyCredentials()
    {
        var user = CreateUser(isApiKey: true);

        var act = () => _tools.ConfirmAgentOperation(user, Guid.NewGuid().ToString());

        act.Should().Throw<UnauthorizedAccessException>()
            .WithMessage("API key credentials cannot confirm pending operations.");
    }

    [Fact]
    public void ConfirmAgentOperation_ThrowsForInvalidGuid()
    {
        var act = () => _tools.ConfirmAgentOperation(CreateUser(), "bad-guid");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*pendingOperationId must be a valid GUID.*");
    }

    [Fact]
    public void ConfirmAgentOperation_ReturnsStoreResult()
    {
        var pendingOperationId = Guid.NewGuid();
        var confirmation = new PendingAgentOperationConfirmation(
            pendingOperationId,
            "agc_token",
            DateTime.UtcNow.AddMinutes(5));
        _pendingOperationStore.Confirm(UserId, pendingOperationId).Returns(confirmation);

        var result = _tools.ConfirmAgentOperation(CreateUser(), pendingOperationId.ToString());

        result.Should().Be(confirmation);
    }

    [Fact]
    public async Task StepUpAgentOperation_ThrowsForApiKeyCredentials()
    {
        var act = () => _tools.StepUpAgentOperation(CreateUser(isApiKey: true), Guid.NewGuid().ToString(), cancellationToken: CancellationToken.None);

        var assertions = await act.Should().ThrowAsync<UnauthorizedAccessException>();
        assertions.WithMessage("API key credentials cannot satisfy step-up authorization.");
    }

    [Fact]
    public async Task StepUpAgentOperation_ThrowsForInvalidGuid()
    {
        var act = () => _tools.StepUpAgentOperation(CreateUser(), "bad-guid", cancellationToken: CancellationToken.None);

        var assertions = await act.Should().ThrowAsync<ArgumentException>();
        assertions.WithMessage("*pendingOperationId must be a valid GUID.*");
    }

    [Fact]
    public async Task StepUpAgentOperation_ReturnsChallenge()
    {
        var pendingOperationId = Guid.NewGuid();
        var challenge = new AgentStepUpChallenge(Guid.NewGuid(), pendingOperationId, DateTime.UtcNow.AddMinutes(10));
        _stepUpService.IssueChallengeAsync(UserId, pendingOperationId, "pt-BR", Arg.Any<CancellationToken>())
            .Returns(Result.Success(challenge));

        var result = await _tools.StepUpAgentOperation(CreateUser(), pendingOperationId.ToString(), "pt-BR", CancellationToken.None);

        result.Should().Be(challenge);
    }

    [Fact]
    public async Task StepUpAgentOperation_ThrowsWhenChallengeCannotBeIssued()
    {
        var pendingOperationId = Guid.NewGuid();
        _stepUpService.IssueChallengeAsync(UserId, pendingOperationId, "en", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AgentStepUpChallenge>("issue_failed"));

        var act = () => _tools.StepUpAgentOperation(CreateUser(), pendingOperationId.ToString(), cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("issue_failed");
    }

    [Fact]
    public async Task VerifyStepUpAgentOperation_ThrowsForApiKeyCredentials()
    {
        var act = () => _tools.VerifyStepUpAgentOperation(
            CreateUser(isApiKey: true),
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            "123456",
            CancellationToken.None);

        var assertions = await act.Should().ThrowAsync<UnauthorizedAccessException>();
        assertions.WithMessage("API key credentials cannot satisfy step-up authorization.");
    }

    [Fact]
    public async Task VerifyStepUpAgentOperation_ThrowsForInvalidPendingOperationId()
    {
        var act = () => _tools.VerifyStepUpAgentOperation(CreateUser(), "bad-guid", Guid.NewGuid().ToString(), "123456", CancellationToken.None);

        var assertions = await act.Should().ThrowAsync<ArgumentException>();
        assertions.WithMessage("*pendingOperationId must be a valid GUID.*");
    }

    [Fact]
    public async Task VerifyStepUpAgentOperation_ThrowsForInvalidChallengeId()
    {
        var act = () => _tools.VerifyStepUpAgentOperation(CreateUser(), Guid.NewGuid().ToString(), "bad-guid", "123456", CancellationToken.None);

        var assertions = await act.Should().ThrowAsync<ArgumentException>();
        assertions.WithMessage("*challengeId must be a valid GUID.*");
    }

    [Fact]
    public async Task VerifyStepUpAgentOperation_ReturnsPendingOperation()
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

        var result = await _tools.VerifyStepUpAgentOperation(
            CreateUser(),
            pendingOperationId.ToString(),
            challengeId.ToString(),
            "123456",
            CancellationToken.None);

        result.Should().Be(pendingOperation);
    }

    [Fact]
    public async Task VerifyStepUpAgentOperation_ThrowsWhenVerificationFails()
    {
        var pendingOperationId = Guid.NewGuid();
        var challengeId = Guid.NewGuid();
        _stepUpService.VerifyChallengeAsync(UserId, pendingOperationId, challengeId, "123456", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<PendingAgentOperation>("verify_failed"));

        var act = () => _tools.VerifyStepUpAgentOperation(
            CreateUser(),
            pendingOperationId.ToString(),
            challengeId.ToString(),
            "123456",
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("verify_failed");
    }

    private static ClaimsPrincipal CreateUser(bool isApiKey = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, UserId.ToString()),
            new("scope", AgentScopes.ReadHabits),
            new("scope", AgentScopes.WriteHabits)
        };

        if (isApiKey)
            claims.Add(new Claim("auth_method", "api_key"));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
