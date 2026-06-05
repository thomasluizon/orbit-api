using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.ChecklistTemplates.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class ChecklistTemplateToolsTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IAgentOperationExecutor _executor = Substitute.For<IAgentOperationExecutor>();
    private readonly ChecklistTemplateTools _tools;
    private readonly ClaimsPrincipal _user;

    public ChecklistTemplateToolsTests()
    {
        _tools = new ChecklistTemplateTools(_mediator, new McpExecutorBridge(_executor));
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private void StubExecutor(AgentOperationStatus status, string? targetId = null, string? policyReason = null)
    {
        var response = new AgentExecuteOperationResponse(new AgentOperationResult(
            "checklist_template", "checklist_template", AgentRiskClass.Low, AgentConfirmationRequirement.None,
            status, TargetId: targetId, PolicyReason: policyReason));

        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(response);
    }

    [Fact]
    public async Task GetChecklistTemplates_Empty_ReturnsNoTemplatesMessage()
    {
        _mediator.Send(Arg.Any<GetChecklistTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ChecklistTemplateResponse>>([]));

        var result = await _tools.GetChecklistTemplates(_user);

        result.Should().Contain("No checklist templates");
    }

    [Fact]
    public async Task GetChecklistTemplates_Success_FormatsTemplates()
    {
        var templates = new List<ChecklistTemplateResponse>
        {
            new(Guid.NewGuid(), "Morning routine", ["brush teeth", "stretch"])
        };
        _mediator.Send(Arg.Any<GetChecklistTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ChecklistTemplateResponse>>(templates));

        var result = await _tools.GetChecklistTemplates(_user);

        result.Should().Contain("Morning routine");
        result.Should().Contain("brush teeth");
    }

    [Fact]
    public async Task CreateChecklistTemplate_RoutesThroughExecutor()
    {
        var templateId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: templateId.ToString());

        var result = await _tools.CreateChecklistTemplate(_user, "Morning routine", "brush teeth, stretch");

        var request = (AgentExecuteOperationRequest)_executor.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAgentOperationExecutor.ExecuteAsync))
            .GetArguments()[0]!;
        request.OperationId.Should().Be("create_checklist_template");
        result.Should().Contain("Created checklist template 'Morning routine'");
    }

    [Fact]
    public async Task DeleteChecklistTemplate_RoutesThroughExecutor()
    {
        var templateId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded);

        var result = await _tools.DeleteChecklistTemplate(_user, templateId.ToString());

        var request = (AgentExecuteOperationRequest)_executor.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAgentOperationExecutor.ExecuteAsync))
            .GetArguments()[0]!;
        request.OperationId.Should().Be("delete_checklist_template");
        result.Should().Contain($"Deleted checklist template {templateId}");
    }
}
