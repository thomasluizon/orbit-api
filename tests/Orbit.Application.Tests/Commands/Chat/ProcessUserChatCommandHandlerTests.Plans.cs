using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Fact]
    public async Task Plan_ProgressEventsDoNotOverlapLegacyToolStepWrites()
    {
        SetupUserAndPayGate();
        _catalogService.GetCapabilityByChatTool(Arg.Any<string>()).Returns(call => BuildCapability(call.Arg<string>()) with { Domain = call.Arg<string>() });
        SetupAiResponse(PlanReadResponse("Your progress", "Read habits", "Read goals"));
        ContinueWithReadDirective("Done");
        var active = 0;
        var overlap = 0;
        var result = await CreateHandler(ReadTool("read_one", new ToolResult(true)), ReadTool("read_two", new ToolResult(true)))
            .Handle(new(UserId, "Read both", ClientContext: new(SupportsToolSteps: true), StreamSink: async _ =>
            {
                if (Interlocked.Increment(ref active) > 1) Interlocked.Increment(ref overlap);
                await Task.Delay(10);
                Interlocked.Decrement(ref active);
            }), default);
        result.IsSuccess.Should().BeTrue();
        overlap.Should().Be(0);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task Plan_TerminalCompoundReadPreservesCompositionWithoutContinuation(string language)
    {
        SetupUserAndPayGate();
        EnableComposition();
        var habits = CompositionTool("query_habits");
        var goals = CompositionTool("query_goals");
        var handler = CreateHandler(habits, goals);
        UseProductionReadExecutor(habits, goals);
        var captured = CapturedCompositionPlan("g8-composition-compound-missing-kind-" + language + "-r1");
        var plan = JsonSerializer.SerializeToElement(new
        {
            title = language == "en" ? "Your progress" : "Seu progresso",
            steps = new[]
            {
                new { text = language == "en" ? "Read habits" : "Ver hábitos", tool = "query_habits", phase = "execution" },
                new { text = language == "en" ? "Read goals" : "Ver metas", tool = "query_goals", phase = "execution" }
            }
        });
        SetupAiResponse(captured with { ToolCalls = [new("present_plan", "plan", plan), .. captured.ToolCalls!] });
        var result = await handler.Handle(new(UserId, "Show habits and goals", ClientContext: new(Locale: language,
            SupportsParts: true, PartKinds: ["habitList", "goalList"], SupportsHabitListCard: true, SupportsGoalListCard: true)), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Parts!.OfType<ChatBlockPart>().Select(part => part.Kind).Should().Equal("habitList", "goalList");
        result.Value.Plan!.Steps.Should().OnlyContain(step => step.Status == "done");
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Plan_WritePreviewKeepsApprovalStepPending()
    {
        SetupUserAndPayGate();
        var tool = FakeTool("bulk_update_habits");
        tool.IsReadOnly.Returns(false);
        var handler = CreateHandler(tool);
        var pending = new PendingAgentOperation(Guid.NewGuid(), AgentCapabilityIds.HabitsBulkWrite,
            "Bulk update", "Update habits", AgentRiskClass.High, AgentConfirmationRequirement.FreshConfirmation, DateTime.UtcNow.AddMinutes(5));
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(new AgentExecuteOperationResponse(
            new("bulk_update_habits", "bulk_update_habits", AgentRiskClass.High, AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.PendingConfirmation), pending));
        var plan = JsonSerializer.SerializeToElement(new
        {
            title = "Your changes",
            steps = new[] { new { text = "Prepare a preview", tool = tool.Name, phase = "execution" }, new { text = "Save your changes", tool = tool.Name, phase = "approval" } }
        });
        SetupAiResponse(new AiResponse { ToolCalls = [new("present_plan", "plan", plan), new(tool.Name, "write", ParseArguments("{}"))], ConversationContext = TestConversationContext });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse { TextMessage = "Confirm first." }));
        var result = await handler.Handle(new(UserId, "Update habits", ClientContext: new(SupportsPendingOperationChanges: true)), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Plan!.Steps.Select(step => step.Status).Should().Equal("done", "pending");
        result.Value.Plan.Steps.Last().PendingOperationId.Should().Be(pending.Id);
        await tool.DidNotReceiveWithAnyArgs().ExecuteAsync(default, default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_PlainTextAndSingleReadOmitOptionalBlock(bool read)
    {
        SetupUserAndPayGate();
        SetupAiResponse(read ? ToolResponse("read_one", "one", "{}") : new AiResponse { TextMessage = "Hello" });
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse { TextMessage = "Done" }));
        var events = new List<ChatStreamEvent>();
        var result = await CreateHandler(ReadTool("read_one", new ToolResult(true))).Handle(new(UserId, "Hello", StreamSink: item =>
        {
            events.Add(item);
            return Task.CompletedTask;
        }), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Plan.Should().BeNull();
        events.Should().NotContain(item => item.Type == "plan" || item.Type == "plan_step");
        JsonDocument.Parse(JsonSerializer.Serialize(result.Value, AiPlan.JsonOptions)).RootElement.TryGetProperty("plan", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Plan_FailedReadEndsCleanlyWithFailedStep()
    {
        SetupUserAndPayGate();
        SetupAiResponse(PlanReadResponse("Your progress", "Read habits", "Read goals"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse { TextMessage = "Done" }));
        var result = await CreateHandler(ReadTool("read_one", new ToolResult(true)), ReadTool("read_two", new ToolResult(false, Error: "read_failed")))
            .Handle(new(UserId, "Read both"), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Plan!.Steps.Select(step => step.Status).Should().Equal("done", "failed");
    }

    [Fact]
    public async Task Plan_RecordingOffStillStreamsButDoesNotStorePlan()
    {
        SetupUserAndPayGate();
        SetupAiResponse(PlanReadResponse("Your progress", "Read habits", "Read goals"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse { TextMessage = "Done" }));
        var result = await CreateHandler(ReadTool("read_one", new ToolResult(true)), ReadTool("read_two", new ToolResult(true)))
            .Handle(new(UserId, "Read both"), default);
        result.Value.Plan.Should().NotBeNull();
        _ = _conversationRecorder.DidNotReceiveWithAnyArgs().RecordAsync(default!);
    }

    [Fact]
    public void Plan_OldClientIgnoresBlockAndAcceptsOldPayload()
    {
        var plan = AiPlan.Create("Your progress", [new("1", "Read habits", "done", "read_one", "execution"),
            new("2", "Read goals", "done", "read_two", "execution")]).Value;
        var response = new ChatResponse("Done", [], Plan: plan);
        var payload = JsonSerializer.Serialize(response, AiPlan.JsonOptions);
        var client = JsonSerializer.Deserialize<OldPlanClientResponse>(payload, AiPlan.JsonOptions);
        client!.AiMessage.Should().Be("Done");
        JsonSerializer.Deserialize<ChatResponse>("{\"aiMessage\":\"Done\",\"actions\":[]}", AiPlan.JsonOptions)!.Plan.Should().BeNull();
        JsonSerializer.Deserialize<OldPlanClientEvent>(new ChatStreamEvent("plan", Plan: plan).ToJson(), AiPlan.JsonOptions)!.Type.Should().Be("plan");
    }

    private sealed record OldPlanClientResponse(string? AiMessage);
    private sealed record OldPlanClientEvent(string Type);

    [Fact]
    public async Task Plan_FastReadCompletesWhileOtherReadIsStillRunning()
    {
        SetupUserAndPayGate();
        var slowResult = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ReadTool("read_one", new ToolResult(true));
        var second = ReadTool("read_two", new ToolResult(true));
        second.ExecuteAsync(Arg.Any<JsonElement>(), UserId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            slowStarted.TrySetResult();
            return slowResult.Task;
        });
        SetupAiResponse(PlanReadResponse("Your progress", "Read habits", "Read goals"));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse { TextMessage = "Done" }));
        var turn = CreateHandler(first, second).Handle(new(UserId, "Read both", StreamSink: item =>
        {
            if (item.Type == "plan_step" && item.PlanStep is { ToolCallId: "one", Status: "done" }) fastCompleted.TrySetResult();
            return Task.CompletedTask;
        }), default);
        try
        {
            await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fastCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            turn.IsCompleted.Should().BeFalse();
        }
        finally
        {
            slowResult.TrySetResult(new ToolResult(true));
            await turn;
        }
    }

    private static AiResponse PlanReadResponse(string title, string firstText, string secondText) => new()
    {
        ToolCalls = [new("present_plan", "plan", JsonSerializer.SerializeToElement(new
        {
            title,
            steps = new[] { new { text = firstText, tool = "read_one", phase = "execution" }, new { text = secondText, tool = "read_two", phase = "execution" } }
        })), new("read_one", "one", ParseArguments("{}")), new("read_two", "two", ParseArguments("{}"))],
        ConversationContext = TestConversationContext
    };

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
