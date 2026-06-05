using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.UserFacts.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class UserFactToolsTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IAgentOperationExecutor _executor = Substitute.For<IAgentOperationExecutor>();
    private readonly UserFactTools _tools;
    private readonly ClaimsPrincipal _user;

    public UserFactToolsTests()
    {
        _tools = new UserFactTools(_mediator, new McpExecutorBridge(_executor));
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private void StubExecutor(AgentOperationStatus status, string? policyReason = null)
    {
        var response = new AgentExecuteOperationResponse(new AgentOperationResult(
            "operation", "operation", AgentRiskClass.Destructive, AgentConfirmationRequirement.FreshConfirmation,
            status, PolicyReason: policyReason));

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
    public async Task GetUserFacts_Success_ReturnsFormattedFacts()
    {
        var facts = new List<UserFactDto>
        {
            new(Guid.NewGuid(), "Prefers morning workouts", "Fitness", DateTime.UtcNow, null)
        };
        _mediator.Send(Arg.Any<GetUserFactsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<UserFactDto>>(facts));

        var result = await _tools.GetUserFacts(_user);

        result.Should().Contain("Prefers morning workouts");
        result.Should().Contain("[Fitness]");
        result.Should().Contain("User Facts (1)");
    }

    [Fact]
    public async Task GetUserFacts_Empty_ReturnsNoFactsMessage()
    {
        _mediator.Send(Arg.Any<GetUserFactsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<UserFactDto>>([]));

        var result = await _tools.GetUserFacts(_user);

        result.Should().Contain("No user facts stored");
    }

    [Fact]
    public async Task GetUserFacts_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetUserFactsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<UserFactDto>>("Error"));

        var result = await _tools.GetUserFacts(_user);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task DeleteUserFact_Success_RoutesToDeleteUserFactsAndReturnsDeletedMessage()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var factId = Guid.NewGuid();
        string result = string.Empty;
        var request = await CapturedRequestAsync(async () => result = await _tools.DeleteUserFact(_user, factId.ToString()));

        request.OperationId.Should().Be("delete_user_facts");
        request.Arguments.GetRawText().Should().Contain("fact_id");
        result.Should().Contain("Deleted user fact");
        result.Should().Contain(factId.ToString());
    }

    [Fact]
    public async Task DeleteUserFact_PendingConfirmation_ReturnsConfirmationPrompt()
    {
        StubExecutor(AgentOperationStatus.PendingConfirmation);

        var result = await _tools.DeleteUserFact(_user, Guid.NewGuid().ToString());

        result.Should().Contain("Confirmation required");
    }

    [Fact]
    public async Task DeleteUserFact_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Fact not found");

        var result = await _tools.DeleteUserFact(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }
}
