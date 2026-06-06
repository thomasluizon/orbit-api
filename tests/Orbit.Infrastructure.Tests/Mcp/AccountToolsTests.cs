using System.Security.Claims;
using FluentAssertions;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class AccountToolsTests
{
    private readonly IAgentOperationExecutor _executor = Substitute.For<IAgentOperationExecutor>();
    private readonly AccountTools _tools;
    private readonly ClaimsPrincipal _user;

    public AccountToolsTests()
    {
        _tools = new AccountTools(new McpExecutorBridge(_executor));
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private void StubExecutor(AgentOperationStatus status, string? targetName = null, string? policyReason = null, Guid? pendingOperationId = null)
    {
        var response = new AgentExecuteOperationResponse(new AgentOperationResult(
            "manage_account", "manage_account", AgentRiskClass.High, AgentConfirmationRequirement.StepUp,
            status, TargetName: targetName, PolicyReason: policyReason, PendingOperationId: pendingOperationId));

        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(response);
    }

    [Fact]
    public async Task ManageAccount_Success_RoutesThroughExecutor()
    {
        StubExecutor(AgentOperationStatus.Succeeded, targetName: "Account reset completed");

        var result = await _tools.ManageAccount(_user, "reset_account");

        var request = (AgentExecuteOperationRequest)_executor.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAgentOperationExecutor.ExecuteAsync))
            .GetArguments()[0]!;
        request.OperationId.Should().Be("manage_account");
        result.Should().Contain("Account reset completed");
    }

    [Fact]
    public async Task ManageAccount_StepUpRequired_ReturnsActionableStepUpMessage()
    {
        StubExecutor(AgentOperationStatus.PendingConfirmation, policyReason: "step_up_required", pendingOperationId: Guid.NewGuid());

        var result = await _tools.ManageAccount(_user, "request_deletion");

        result.Should().Contain("Step-up verification required");
        result.Should().Contain("step_up_agent_operation_v2");
    }
}
