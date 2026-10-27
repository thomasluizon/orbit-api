using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class ChatControllerTests
{
    [Theory]
    [InlineData(false, "pt-BR", null, "pt-BR")]
    [InlineData(true, "pt-BR", null, "pt-BR")]
    [InlineData(false, "en-US", null, "en")]
    [InlineData(true, "pt-BR;q=0.5,en;q=1", null, "en")]
    [InlineData(false, "pt-BR", "{\"locale\":\"en\",\"supportsParts\":true}", "en")]
    [InlineData(true, "en", "{\"locale\":\"pt-BR\",\"supportsParts\":true}", "pt-BR")]
    public async Task Chat_RequestLocalePrefersClientThenAcceptLanguage(bool stream, string header, string? context, string expected)
    {
        _controller.Request.Headers.AcceptLanguage = header;
        _controller.Response.Body = new MemoryStream();
        _mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ChatResponse("Reply", [])));

        if (stream)
            await _controller.ProcessChatStream("Hello", null, null, default, clientContext: context);
        else
            await _controller.ProcessChat("Hello", null, null, default, clientContext: context);

        await _mediator.Received(1).Send(Arg.Is<ProcessUserChatCommand>(command =>
            command.ClientContext != null && command.ClientContext.Locale == expected
            && (context == null || command.ClientContext.SupportsParts == true)), Arg.Any<CancellationToken>());
    }
}
