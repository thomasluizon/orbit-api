using System.Security.Claims;
using FluentAssertions;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class SupportToolsTests
{
    private readonly IAgentOperationExecutor _executor = Substitute.For<IAgentOperationExecutor>();
    private readonly SupportTools _tools;
    private readonly ClaimsPrincipal _user;

    public SupportToolsTests()
    {
        _tools = new SupportTools(new McpExecutorBridge(_executor));
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private void StubExecutor(AgentOperationStatus status, string? policyReason = null)
    {
        var response = new AgentExecuteOperationResponse(new AgentOperationResult(
            "send_support_request", "send_support_request", AgentRiskClass.Low, AgentConfirmationRequirement.None,
            status, PolicyReason: policyReason));

        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(response);
    }

    [Fact]
    public async Task SendSupportRequest_Success_RoutesThroughExecutor()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var result = await _tools.SendSupportRequest(_user, "Name", "name@example.com", "Subject", "Message");

        var request = (AgentExecuteOperationRequest)_executor.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAgentOperationExecutor.ExecuteAsync))
            .GetArguments()[0]!;
        request.OperationId.Should().Be("send_support_request");
        result.Should().Be("Support request sent.");
    }

    [Fact]
    public async Task SendSupportRequest_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Email service unavailable");

        var result = await _tools.SendSupportRequest(_user, "Name", "name@example.com", "Subject", "Message");

        result.Should().StartWith("Error: ");
    }
}
