using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class RecordPageToolsTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IAgentOperationExecutor _executor = Substitute.For<IAgentOperationExecutor>();

    private ClaimsPrincipal User => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "Test"));

    private RecordPageTools Tools => new(_mediator, new McpExecutorBridge(_executor));

    [Theory]
    [InlineData("tags")]
    [InlineData("templates")]
    public async Task ReferencePage_ReturnsAuthorizedCard(string kind)
    {
        var cursor = RecordListCursor.Create(_userId, kind, 10);
        var card = new RecordListCard(kind, 11, [new RecordListItem("record", "Visible")]);
        GetRecordListPageQuery? captured = null;
        _mediator.Send(Arg.Do<GetRecordListPageQuery>(query => captured = query), Arg.Any<CancellationToken>())
            .Returns(Result.Success(card));

        var result = kind == "tags"
            ? await Tools.GetTagPage(User, cursor)
            : await Tools.GetTemplatePage(User, cursor);

        result.Should().Contain("Visible");
        captured.Should().Be(new GetRecordListPageQuery(_userId, kind, cursor));
    }

    [Fact]
    public async Task PageFailure_ReturnsErrorWithoutCard()
    {
        _mediator.Send(Arg.Any<GetRecordListPageQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RecordListCard>("Record page not found."));

        var result = await Tools.GetTagPage(User, RecordListCursor.Create(_userId, "tags", 10));

        result.Should().Be("Error: Record page not found.");
    }

    [Fact]
    public async Task ApiKeyPage_RequiresAccountBoundCursorAndExecutorSuccess()
    {
        var tools = Tools;
        var foreign = await tools.GetApiKeyPage(User, RecordListCursor.Create(Guid.NewGuid(), "keys", 10));
        foreign.Should().Be("Record page not found.");
        await _executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);

        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new AgentOperationResult(
                "get_api_keys", "get_api_keys", AgentRiskClass.Low, AgentConfirmationRequirement.None,
                AgentOperationStatus.Failed, PolicyReason: "Pro required")));
        var cursor = RecordListCursor.Create(_userId, "keys", 10);
        (await tools.GetApiKeyPage(User, cursor)).Should().Contain("Pro required");

        var now = DateTime.UtcNow;
        IReadOnlyList<ApiKeyResponse> keys = Enumerable.Range(0, 11).Select(index =>
            new ApiKeyResponse(Guid.NewGuid(), $"Key {index}", $"orb_{index}", ["read"], true,
                null, now, null, false)).ToList();
        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentExecuteOperationResponse(new AgentOperationResult(
                "get_api_keys", "get_api_keys", AgentRiskClass.Low, AgentConfirmationRequirement.None,
                AgentOperationStatus.Succeeded, Payload: keys)));

        var result = await tools.GetApiKeyPage(User, cursor);

        result.Should().Contain("Key 10");
        result.Should().NotContain("Key 9");
        (await tools.GetApiKeyPage(User, RecordListCursor.Create(_userId, "keys", 20)))
            .Should().Be("Record page not found.");
    }
}
