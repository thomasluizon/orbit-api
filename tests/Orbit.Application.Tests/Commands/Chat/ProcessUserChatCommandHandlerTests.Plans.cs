using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", "Your progress", "Read habits", "Read goals")]
    [InlineData("pt-BR", "Seu progresso", "Ver hábitos", "Ver metas")]
    public async Task Plan_TwoReads_StreamsBeforeExecutionAndRecordsFinalStatuses(string language, string title, string firstText, string secondText)
    {
        SetupUserAndPayGate();
        EnableRecording();
        var events = new List<ChatStreamEvent>();
        var first = ReadTool("read_one", new ToolResult(true));
        var second = ReadTool("read_two", new ToolResult(true));
        first.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            events.Should().Contain(item => item.Type == "plan");
            return new ToolResult(true);
        });
        var plan = JsonSerializer.SerializeToElement(new
        {
            title,
            steps = new[] { new { text = firstText, tool = "read_one", phase = "execution" }, new { text = secondText, tool = "read_two", phase = "execution" } }
        });
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new("present_plan", "plan", plan), new("read_one", "one", ParseArguments("{}")), new("read_two", "two", ParseArguments("{}"))],
            ConversationContext = TestConversationContext
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse { TextMessage = "Done" }));
        ConversationTurnCapture? capture = null;
        _conversationRecorder.RecordAsync(Arg.Do<ConversationTurnCapture>(turn => capture = turn)).Returns(true);

        var result = await CreateHandler(first, second).Handle(new(UserId, "Read both", ClientContext: new(Locale: language), StreamSink: item =>
        {
            events.Add(item);
            return Task.CompletedTask;
        }), default);

        result.IsSuccess.Should().BeTrue();
        events.Count(item => item.Type == "plan").Should().Be(1);
        using var recorded = JsonDocument.Parse(capture!.ResponseJson!);
        var final = recorded.RootElement.GetProperty("plan");
        final.GetProperty("title").GetString().Should().Be(title);
        final.GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("status").GetString()).Should().Equal("done", "done");
        var changes = events.Where(item => item.Type == "plan_step").Select(item => JsonDocument.Parse(item.ToJson()).RootElement.Clone()).ToArray();
        changes.Select(item => item.GetProperty("planStep").GetProperty("status").GetString()).Should().Equal("running", "running", "done", "done");
        await _aiIntentService.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Any(item => item.Name == "present_plan" && item.Success)), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }
}
