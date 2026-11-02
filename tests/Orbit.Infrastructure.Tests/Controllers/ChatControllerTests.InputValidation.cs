using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class ChatControllerTests
{
    [Theory]
    [InlineData("message", "MESSAGE_TOO_LONG")]
    [InlineData("count", "CHAT_HISTORY_TOO_LARGE")]
    [InlineData("role", "INVALID_CHAT_HISTORY")]
    [InlineData("content", "INVALID_CHAT_HISTORY")]
    public async Task InvalidChatInput_ReturnsOneLegacyErrorBeforeStreaming(string rule, string expectedCode)
    {
        foreach (var transport in new[] { "json", "stream", "replay" })
        {
            using var turns = new ChatTurnStreamTestServices(_mediator, UserId);
            _controller.HttpContext.RequestServices = turns.Provider;
            var message = rule == "message" ? new string('a', AppConstants.MaxChatMessageLength + 1) : "Hello";
            var history = rule switch
            {
                "count" => Enumerable.Repeat(new { role = "user", content = "Hello" }, AppConstants.MaxChatHistoryMessages + 1),
                "role" => [new { role = "system", content = "Hello" }],
                "content" => [new { role = "user", content = new string('a', AppConstants.MaxChatHistoryMessageLength + 1) }],
                _ => null
            };
            var json = history is null ? null : JsonSerializer.Serialize(history);
            var result = transport == "json"
                ? await _controller.ProcessChat(message, json, null, default)
                : await _controller.ProcessChatStream(message, json, null, default,
                    idempotencyKey: transport == "replay" ? "invalid-turn" : null);
            var response = result.Should().BeOfType<BadRequestObjectResult>().Which;
            var body = JsonSerializer.SerializeToElement(response.Value);
            body.GetProperty("errorCode").GetString().Should().Be(expectedCode);
            _controller.Response.HasStarted.Should().BeFalse();
            _controller.Response.ContentType.Should().NotBe("text/event-stream");
        }
        await _mediator.DidNotReceive().Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>());
    }
}
