using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class ChatControllerTests
{
    [Theory]
    [InlineData(false, "en", "Study", "Open the book")]
    [InlineData(true, "pt-BR", "Estudar", "Abrir o livro")]
    public async Task Chat_PreviousStartingStep_ReachesCommand(bool stream, string language, string goal, string step)
    {
        var previous = new StartingStep(goal, step, language);
        var context = JsonSerializer.Serialize(new AgentClientContext(PreviousStartingStep: previous), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _controller.Response.Body = new MemoryStream();
        _mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ChatResponse(null, [])));

        if (stream)
            await _controller.ProcessChatStream("...", null, null, default, clientContext: context);
        else
            await _controller.ProcessChat("...", null, null, default, clientContext: context);

        await _mediator.Received(1).Send(Arg.Is<ProcessUserChatCommand>(command =>
            command.ClientContext != null && command.ClientContext.PreviousStartingStep == previous), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"goal\":\"Study\",\"step\":\"\",\"language\":\"en\"}")]
    [InlineData("{\"goal\":\"Study\",\"step\":\"Open\\nRead\",\"language\":\"en\"}")]
    [InlineData("{\"goal\":\"Study\",\"step\":\"Open\",\"language\":\"es\"}")]
    public async Task Chat_InvalidPreviousStartingStep_ReturnsBadRequest(string previous)
    {
        var context = "{\"previousStartingStep\":" + previous + "}";

        var response = await _controller.ProcessChat("...", null, null, default, clientContext: context);

        response.Should().BeOfType<BadRequestObjectResult>();
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(ProcessUserChatCommand)!, default);
    }
}
