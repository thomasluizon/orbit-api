using System.Security.Claims;
using FluentAssertions;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class ApiKeyToolsTests
{
    private readonly IAgentOperationExecutor _executor = Substitute.For<IAgentOperationExecutor>();
    private readonly ApiKeyTools _tools;
    private readonly ClaimsPrincipal _user;

    public ApiKeyToolsTests()
    {
        _tools = new ApiKeyTools(new McpExecutorBridge(_executor));
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private void StubExecutor(
        AgentOperationStatus status,
        string operationId = "manage_api_keys",
        string? targetId = null,
        string? targetName = null,
        string? policyReason = null,
        Guid? pendingOperationId = null,
        object? payload = null)
    {
        var response = new AgentExecuteOperationResponse(new AgentOperationResult(
            operationId, operationId, AgentRiskClass.High, AgentConfirmationRequirement.StepUp,
            status, TargetId: targetId, TargetName: targetName, PolicyReason: policyReason,
            PendingOperationId: pendingOperationId, Payload: payload));

        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(response);
    }

    private AgentExecuteOperationRequest SingleExecutorRequest() =>
        (AgentExecuteOperationRequest)_executor.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAgentOperationExecutor.ExecuteAsync))
            .GetArguments()[0]!;

    [Fact]
    public async Task GetApiKeys_Empty_ReturnsNoKeysMessage()
    {
        StubExecutor(
            AgentOperationStatus.Succeeded,
            operationId: "get_api_keys",
            payload: (IReadOnlyList<ApiKeyResponse>)[]);

        var result = await _tools.GetApiKeys(_user);

        result.Should().Contain("No API keys");
    }

    [Fact]
    public async Task GetApiKeys_Success_RoutesThroughExecutorAndFormatsKeys()
    {
        var keys = new List<ApiKeyResponse>
        {
            new(Guid.NewGuid(), "CI key", "orbit_ab", ["read_habits"], true, null, DateTime.UtcNow, null, false)
        };
        StubExecutor(
            AgentOperationStatus.Succeeded,
            operationId: "get_api_keys",
            payload: (IReadOnlyList<ApiKeyResponse>)keys);

        var result = await _tools.GetApiKeys(_user);

        SingleExecutorRequest().OperationId.Should().Be("get_api_keys");
        result.Should().Contain("CI key");
        result.Should().Contain("read-only");
        result.Should().Contain("read_habits");
    }

    [Fact]
    public async Task GetApiKeys_Failure_ReturnsError()
    {
        StubExecutor(
            AgentOperationStatus.Failed,
            operationId: "get_api_keys",
            policyReason: "Pro required");

        var result = await _tools.GetApiKeys(_user);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetApiKeys_StepUpRequired_ReturnsActionableStepUpMessage()
    {
        StubExecutor(
            AgentOperationStatus.PendingConfirmation,
            operationId: "get_api_keys",
            policyReason: "step_up_required",
            pendingOperationId: Guid.NewGuid());

        var result = await _tools.GetApiKeys(_user);

        result.Should().Contain("Step-up verification required");
    }

    [Fact]
    public async Task GetApiKeys_ForwardsTheConfirmationToken()
    {
        StubExecutor(
            AgentOperationStatus.Succeeded,
            operationId: "get_api_keys",
            payload: (IReadOnlyList<ApiKeyResponse>)[]);

        await _tools.GetApiKeys(_user, confirmationToken: "agc_token");

        SingleExecutorRequest().ConfirmationToken.Should().Be("agc_token");
    }

    [Fact]
    public async Task ManageApiKeys_Create_RoutesThroughExecutor()
    {
        var keyId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: keyId.ToString(), targetName: "CI key");

        var result = await _tools.ManageApiKeys(_user, "create", name: "CI key", scopes: "read_habits,write_habits");

        SingleExecutorRequest().OperationId.Should().Be("manage_api_keys");
        result.Should().Contain("Created API key 'CI key'");
    }

    [Fact]
    public async Task ManageApiKeys_StepUpRequired_ReturnsActionableStepUpMessage()
    {
        StubExecutor(AgentOperationStatus.PendingConfirmation, policyReason: "step_up_required", pendingOperationId: Guid.NewGuid());

        var result = await _tools.ManageApiKeys(_user, "revoke", keyId: Guid.NewGuid().ToString());

        result.Should().Contain("Step-up verification required");
    }
}
