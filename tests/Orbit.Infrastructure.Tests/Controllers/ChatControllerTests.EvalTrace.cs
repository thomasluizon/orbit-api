using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class ChatControllerTests
{
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    [InlineData(false, "1", true)]
    [InlineData(true, "1", true)]
    [InlineData(false, "true", false)]
    [InlineData(true, "0", false)]
    [InlineData(false, "1,1", false)]
    public async Task EvalTraceHeader_IsMappedToRequestOptIn(bool stream, string? header, bool requested)
    {
        _controller.HttpContext.TraceIdentifier = "http-eval-request";
        _controller.Response.Body = new MemoryStream();
        if (header is not null)
            _controller.Request.Headers["X-Orbit-Eval-Trace"] = header;
        _mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ChatResponse("Done", [])));

        if (stream)
            await _controller.ProcessChatStream("Find my habits", null, null, CancellationToken.None);
        else
            await _controller.ProcessChat("Find my habits", null, null, CancellationToken.None);

        await _mediator.Received(1).Send(Arg.Is<ProcessUserChatCommand>(command =>
            command.EvalTraceRequested == requested && command.CorrelationId == "http-eval-request"
            && command.UserId == UserId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvalTrace_StreamFinalCarriesTheResponseTrace()
    {
        var trace = new ChatEvalTrace("request-trace", []);
        _controller.Request.Headers["X-Orbit-Eval-Trace"] = "1";
        _controller.Response.Body = new MemoryStream();
        _mediator.Send(Arg.Any<ProcessUserChatCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ChatResponse("Done", [], EvalTrace: trace)));

        await _controller.ProcessChatStream("Find my habits", null, null, CancellationToken.None);

        _controller.Response.Body.Position = 0;
        var body = await new StreamReader(_controller.Response.Body).ReadToEndAsync();
        var finalLine = body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal)
            && line.Contains("\"type\":\"final\"", StringComparison.Ordinal));
        using var final = JsonDocument.Parse(finalLine[6..]);
        final.RootElement.GetProperty("response").GetProperty("evalTrace").GetProperty("traceId")
            .GetString().Should().Be("request-trace");
        final.RootElement.GetProperty("evalTrace").GetRawText().Should()
            .Be(final.RootElement.GetProperty("response").GetProperty("evalTrace").GetRawText());
    }
}
