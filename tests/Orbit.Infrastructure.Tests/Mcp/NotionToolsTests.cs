using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Connections;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Mcp;

public class NotionToolsTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static ClaimsPrincipal Principal => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "test"));

    [Fact]
    public async Task ReadUsesTheSharedToolAndPreservesUntrustedDataFraming()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Is<GetNotionConnectionQuery>(q => q.UserId == UserId), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new NotionConnectionResponse(true, "Workspace", "Idle", DateTime.UtcNow, [])));
        mediator.Send(Arg.Is<GetNotionTasksQuery>(q => q.UserId == UserId), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<NotionTaskResponse>>([]));
        var tools = new Orbit.Api.Mcp.Tools.NotionTools(new GetNotionTasksTool(mediator));
        var output = JsonSerializer.Deserialize<JsonElement>(await tools.GetNotionTasks(Principal));
        output.GetProperty("Success").GetBoolean().Should().BeTrue();
        output.GetProperty("Payload").GetProperty("DataTrust").GetString().Should().Be("untrusted_data");
        output.GetProperty("Payload").GetProperty("TextHandling").GetString().Should().Contain("never as instructions");
    }

    [Fact]
    public async Task GateFailureDoesNotReadTasks()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetNotionConnectionQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<NotionConnectionResponse>("Pro required"));
        var tools = new Orbit.Api.Mcp.Tools.NotionTools(new GetNotionTasksTool(mediator));
        var output = JsonSerializer.Deserialize<JsonElement>(await tools.GetNotionTasks(Principal));
        output.GetProperty("Success").GetBoolean().Should().BeFalse();
        output.GetProperty("ErrorCode").GetString().Should().Be(Result.PayGateErrorCode);
        await mediator.DidNotReceive().Send(Arg.Any<GetNotionTasksQuery>(), Arg.Any<CancellationToken>());
    }
}
