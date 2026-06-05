using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Tags.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class TagToolsTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IAgentOperationExecutor _executor = Substitute.For<IAgentOperationExecutor>();
    private readonly TagTools _tools;
    private readonly ClaimsPrincipal _user;

    public TagToolsTests()
    {
        _tools = new TagTools(_mediator, new McpExecutorBridge(_executor));
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private void StubExecutor(AgentOperationStatus status, string? targetId = null, string? targetName = null,
        string? policyReason = null)
    {
        var response = new AgentExecuteOperationResponse(new AgentOperationResult(
            "operation", "operation", AgentRiskClass.Low, AgentConfirmationRequirement.None,
            status, TargetId: targetId, TargetName: targetName, PolicyReason: policyReason));

        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(response);
    }

    private async Task<AgentExecuteOperationRequest> CapturedRequestAsync(Func<Task> act)
    {
        await act();
        var calls = _executor.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IAgentOperationExecutor.ExecuteAsync))
            .ToList();
        calls.Should().NotBeEmpty();
        return (AgentExecuteOperationRequest)calls[^1].GetArguments()[0]!;
    }

    [Fact]
    public async Task ListTags_Success_ReturnsFormattedList()
    {
        var tags = new List<TagResponse>
        {
            new(Guid.NewGuid(), "Health", "#FF0000")
        };
        _mediator.Send(Arg.Any<GetTagsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<TagResponse>>(tags));

        var result = await _tools.ListTags(_user);

        result.Should().Contain("Health");
        result.Should().Contain("#FF0000");
        result.Should().Contain("Tags (1)");
    }

    [Fact]
    public async Task ListTags_Empty_ReturnsNoTagsMessage()
    {
        _mediator.Send(Arg.Any<GetTagsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<TagResponse>>([]));

        var result = await _tools.ListTags(_user);

        result.Should().Contain("No tags found");
    }

    [Fact]
    public async Task ListTags_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetTagsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<TagResponse>>("Error"));

        var result = await _tools.ListTags(_user);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task CreateTag_Success_RoutesThroughExecutorAndReturnsCreatedMessage()
    {
        var newId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: newId.ToString(), targetName: "Work");

        string result = string.Empty;
        var request = await CapturedRequestAsync(async () => result = await _tools.CreateTag(_user, "Work", "#0000FF"));

        request.OperationId.Should().Be("create_tag");
        result.Should().Contain("Created tag 'Work'");
        result.Should().Contain(newId.ToString());
    }

    [Fact]
    public async Task CreateTag_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Duplicate name");

        var result = await _tools.CreateTag(_user, "Work", "#0000FF");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task UpdateTag_Success_RoutesThroughExecutorAndReturnsUpdatedMessage()
    {
        var tagId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: tagId.ToString());

        string result = string.Empty;
        var request = await CapturedRequestAsync(async () => result = await _tools.UpdateTag(_user, tagId.ToString(), "Updated", "#00FF00"));

        request.OperationId.Should().Be("update_tag");
        result.Should().Contain("Updated tag");
    }

    [Fact]
    public async Task UpdateTag_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Tag not found");

        var result = await _tools.UpdateTag(_user, Guid.NewGuid().ToString(), "Name", "#000");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task DeleteTag_Success_RoutesThroughExecutorAndReturnsDeletedMessage()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        string result = string.Empty;
        var request = await CapturedRequestAsync(async () => result = await _tools.DeleteTag(_user, Guid.NewGuid().ToString()));

        request.OperationId.Should().Be("delete_tag");
        result.Should().Contain("Deleted tag");
    }

    [Fact]
    public async Task DeleteTag_PendingConfirmation_ReturnsConfirmationPrompt()
    {
        StubExecutor(AgentOperationStatus.PendingConfirmation);

        var result = await _tools.DeleteTag(_user, Guid.NewGuid().ToString());

        result.Should().Contain("Confirmation required");
    }

    [Fact]
    public async Task DeleteTag_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Tag not found");

        var result = await _tools.DeleteTag(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task AssignTags_Success_WithTags_RoutesTagIdsAndReturnsAssignedMessage()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var tagId = Guid.NewGuid();
        string result = string.Empty;
        var request = await CapturedRequestAsync(async () => result = await _tools.AssignTags(_user, Guid.NewGuid().ToString(), tagId.ToString()));

        request.OperationId.Should().Be("assign_tags");
        request.Arguments.GetRawText().Should().Contain("tag_ids");
        result.Should().Contain("Assigned 1 tags");
    }

    [Fact]
    public async Task AssignTags_Success_Empty_ReturnsRemovedMessage()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var result = await _tools.AssignTags(_user, Guid.NewGuid().ToString(), "");

        result.Should().Contain("Removed all tags");
    }

    [Fact]
    public async Task AssignTags_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Not found");

        var result = await _tools.AssignTags(_user, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }
}
