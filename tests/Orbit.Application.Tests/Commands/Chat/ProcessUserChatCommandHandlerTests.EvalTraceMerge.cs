using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.Common;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Handle_EvalTrace_AndConversationRecordingAreIndependent(bool requested, bool traceFlag, bool recordingFlag)
    {
        SetupUserAndPayGate();
        var flags = new List<string>();
        if (traceFlag)
            flags.Add(FeatureFlagKeys.AstraEvalTrace);
        if (recordingFlag)
            flags.Add(FeatureFlagKeys.AstraConversationRecording);
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>()).Returns(flags);
        var first = ReadTool("query_habits", new ToolResult(true));
        var second = ReadTool("query_goals", new ToolResult(true));
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new("query_habits", "one", ParseArguments("{\"limit\":1}")),
                new("query_goals", "two", ParseArguments("{\"limit\":2}"))],
            ConversationContext = TestConversationContext
        });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Done" }));
        ConversationTurnCapture? capture = null;
        _conversationRecorder.TryRecord(Arg.Do<ConversationTurnCapture>(turn => capture = turn)).Returns(true);

        var result = await CreateHandler(first, second).Handle(new ProcessUserChatCommand(UserId, "Find my habits and goals",
            EvalTraceRequested: requested), default);

        result.IsSuccess.Should().BeTrue();
        if (requested && traceFlag)
            result.Value.EvalTrace!.ToolCalls.Select(call => call.Id).Should().Equal("one", "two");
        else
            result.Value.EvalTrace.Should().BeNull();
        if (recordingFlag)
        {
            _conversationRecorder.Received(1).TryRecord(Arg.Any<ConversationTurnCapture>());
            var calls = JsonSerializer.Deserialize<ConversationToolCall[]>(capture!.ToolCallsJson)!;
            calls.Select(call => call.CallId).Should().Equal("one", "two");
            calls.Select(call => call.Arguments.GetProperty("limit").GetInt32()).Should().Equal(1, 2);
            capture.ToolCallsJson.Should().NotContain("EvalTrace").And.NotContain("evalTrace");
        }
        else
            _conversationRecorder.DidNotReceiveWithAnyArgs().TryRecord(default!);
    }

    [Theory]
    [InlineData("update_checklist_template", false)]
    [InlineData("update_checklist_template", true)]
    [InlineData("update_profile_preferences", false)]
    [InlineData("update_profile_preferences", true)]
    [InlineData("manage_calendar_sync", false)]
    [InlineData("manage_calendar_sync", true)]
    public async Task Handle_EvalTrace_PreservesArgumentsAndResultsFromExpandedTools(string toolName, bool stream)
    {
        SetupUserAndPayGate();
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.AstraEvalTrace });
        _mediator.Send(Arg.Any<UpdateChecklistTemplateCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _mediator.Send(Arg.Any<SetNameCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _mediator.Send(Arg.Any<SetSelectedCalendarsCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        (IAiTool tool, JsonElement arguments) = toolName switch
        {
            "update_checklist_template" => ((IAiTool)new UpdateChecklistTemplateTool(_mediator),
                JsonSerializer.SerializeToElement(new { template_id = Guid.NewGuid(), name = "Packing", items = new[] { "Passport" } })),
            "update_profile_preferences" => ((IAiTool)new UpdateProfilePreferencesTool(_mediator),
                JsonSerializer.SerializeToElement(new { action = "set_name", name = "Eval Account" })),
            _ => ((IAiTool)new ManageCalendarSyncTool(_mediator),
                JsonSerializer.SerializeToElement(new { action = "set_selected_calendars", calendar_ids = new[] { "primary" } }))
        };
        SetupReadToolReply(toolName, "Updated.");
        SetupAiResponse(new AiResponse
        {
            ToolCalls = [new(toolName, "expanded-call", arguments)],
            ConversationContext = TestConversationContext
        });

        var result = await CreateHandler(tool).Handle(new ProcessUserChatCommand(UserId, "Update my account preferences",
            EvalTraceRequested: true, StreamSink: stream ? _ => Task.CompletedTask : null), default);

        result.IsSuccess.Should().BeTrue();
        var call = EvalResponseJson(result.Value, stream).GetProperty("evalTrace").GetProperty("toolCalls")[0];
        call.GetProperty("name").GetString().Should().Be(toolName);
        call.GetProperty("arguments").GetRawText().Should().Be(arguments.GetRawText());
        call.GetProperty("status").GetString().Should().Be("Succeeded");
        call.GetProperty("result").GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_EvalTrace_RecordsOriginalBulkCallsOnceWhenCoalescedIntoOnePreview(bool stream)
    {
        SetupResolutionAccount();
        _featureFlagService.GetEnabledKeysForUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.AstraEvalTrace });
        var tool = new BulkLogHabitsTool(_mediator, _habitRepo, _userDateService,
            new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), _unitOfWork));
        var handler = CreateHandler(tool);
        var held = HoldReplayWrites(tool);
        var modelCalls = Enumerable.Range(1, 6).Select(day => new AiToolCall(tool.Name, "log" + day,
            JsonSerializer.SerializeToElement(new { habit_ids = new[] { "água" }, date = $"2026-04-0{day}" }))).ToArray();
        SetupAiResponse(new AiResponse { ToolCalls = modelCalls, ConversationContext = TestConversationContext });
        ReplyToReplay("Revise os registros.");

        var result = await handler.Handle(new ProcessUserChatCommand(UserId, "Registre água.", EvalTraceRequested: true,
            StreamSink: stream ? _ => Task.CompletedTask : null), default);

        result.IsSuccess.Should().BeTrue();
        held.Should().ContainSingle();
        result.Value.PendingOperations.Should().ContainSingle();
        var calls = result.Value.EvalTrace!.ToolCalls;
        calls.Select(call => call.Id).Should().Equal(modelCalls.Select(call => call.Id));
        calls.Select(call => call.Name).Should().OnlyContain(name => name == tool.Name);
        calls.Select(call => call.Arguments.GetRawText()).Should().Equal(modelCalls.Select(call => call.Args.GetRawText()));
        calls[0].Status.Should().Be("PendingConfirmation");
        calls.Skip(1).Should().OnlyContain(call => call.Status == "Succeeded");
    }
}
