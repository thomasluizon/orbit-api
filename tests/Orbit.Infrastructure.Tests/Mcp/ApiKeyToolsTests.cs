using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class ApiKeyToolsTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IAgentOperationExecutor _executor = Substitute.For<IAgentOperationExecutor>();
    private readonly ApiKeyTools _tools;
    private readonly ClaimsPrincipal _user;

    public ApiKeyToolsTests()
    {
        _tools = new ApiKeyTools(_mediator, new McpExecutorBridge(_executor));
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private void StubExecutor(AgentOperationStatus status, string? targetId = null, string? targetName = null, string? policyReason = null, Guid? pendingOperationId = null)
    {
        var response = new AgentExecuteOperationResponse(new AgentOperationResult(
            "manage_api_keys", "manage_api_keys", AgentRiskClass.High, AgentConfirmationRequirement.StepUp,
            status, TargetId: targetId, TargetName: targetName, PolicyReason: policyReason, PendingOperationId: pendingOperationId));

        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(response);
    }

    [Fact]
    public async Task GetApiKeys_Empty_ReturnsNoKeysMessage()
    {
        _mediator.Send(Arg.Any<GetApiKeysQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ApiKeyResponse>>([]));

        var result = await _tools.GetApiKeys(_user);

        result.Should().Contain("No API keys");
    }

    [Fact]
    public async Task GetApiKeys_Success_FormatsKeys()
    {
        var keys = new List<ApiKeyResponse>
        {
            new(Guid.NewGuid(), "CI key", "orbit_ab", ["read_habits"], true, null, DateTime.UtcNow, null, false)
        };
        _mediator.Send(Arg.Any<GetApiKeysQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ApiKeyResponse>>(keys));

        var result = await _tools.GetApiKeys(_user);

        result.Should().Contain("CI key");
        result.Should().Contain("read-only");
        result.Should().Contain("read_habits");
    }

    [Fact]
    public async Task GetApiKeys_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetApiKeysQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<ApiKeyResponse>>("Pro required"));

        var result = await _tools.GetApiKeys(_user);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task ManageApiKeys_Create_RoutesThroughExecutor()
    {
        var keyId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: keyId.ToString(), targetName: "CI key");

        var result = await _tools.ManageApiKeys(_user, "create", name: "CI key", scopes: "read_habits,write_habits");

        var request = (AgentExecuteOperationRequest)_executor.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAgentOperationExecutor.ExecuteAsync))
            .GetArguments()[0]!;
        request.OperationId.Should().Be("manage_api_keys");
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
