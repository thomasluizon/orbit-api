using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("pt-BR", false, true)]
    [InlineData("pt-BR", true, true)]
    [InlineData("en", false, true)]
    [InlineData("en", true, true)]
    [InlineData("pt-BR", false, false)]
    [InlineData("pt-BR", true, false)]
    public async Task EmptyContinuation_RevisedRoutineUsesCurrentPreviewAndSharedComposition(
        string language, bool sameTurn, bool empty)
    {
        _user.SetLanguage(language);
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.AstraBlockFirst });
        const string arguments = """
            {"title":"Evening routine","frequency_unit":"Day","due_time":"21:00","sub_habits":[
                {"title":"Read","frequency_unit":"Day","due_time":"21:30","due_end_time":"21:50","description":"Read for 20 minutes"},
                {"title":"Stretch","frequency_unit":"Day","due_time":"21:50","due_end_time":"22:00","description":"Stretch for 10 minutes"},
                {"title":"Lights out","frequency_unit":"Day","due_time":"23:00"}]}
            """;
        ScriptToolCall(arguments);
        PendingAgentOperation? original = null;
        string? originalOperation = null;
        if (!sameTurn)
        {
            var held = await CreateHandler().Handle(new(_userId, "Create an evening routine"), default);
            original = held.Value.PendingOperations.Should().ContainSingle().Which;
            originalOperation = _pendingOperationStore.GetExecution(_userId, original.Id)!.OperationId;
        }
        AiResponse RevisionCall(PendingAgentOperation preview) => new()
        {
            ToolCalls = [new("revise_pending_changes", "revise", JsonSerializer.SerializeToElement(new
            {
                pending_operation_id = preview.Id,
                items = new[] { new { item_id = preview.Items!.Single().ItemId,
                    edits_json = "{\"due_time\":\"21:30\"}" } }
            }))],
            ConversationContext = new()
        };
        if (original is not null)
            _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
                Arg.Any<CancellationToken>()).Returns(Result.Success(RevisionCall(original)));
        var rounds = 0;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var output = call.ArgAt<IReadOnlyList<AiToolCallResult>>(1).Should().ContainSingle().Which;
                output.Success.Should().BeTrue(output.Error);
                if (sameTurn && ++rounds == 1)
                {
                    var stored = _pendingOperationStore.GetAwaiting(_userId).Should().ContainSingle().Which;
                    originalOperation = stored.OperationId;
                    var preview = await _previewer.PreviewAsync(_userId, stored.OperationId, stored.Arguments, default);
                    original = new(stored.PendingOperationId, stored.CapabilityId, stored.OperationId, stored.OperationId,
                        AgentRiskClass.Low, stored.ConfirmationRequirement, stored.ExpiresAtUtc!.Value,
                        preview.Value!.Changes, preview.Value.ChangeTargetCount, preview.Value.Items, preview.Value.PreviewFingerprint);
                    return Result.Success(RevisionCall(original));
                }
                output.Name.Should().Be("revise_pending_changes");
                var revised = _pendingOperationStore.GetAwaiting(_userId).Should().ContainSingle().Which;
                RoutineArguments(revised.Arguments).GetProperty("due_time").GetString().Should().Be("21:30");
                return empty ? Result.Failure<AiResponse>(ErrorMessages.AiEmptyResponse)
                    : Result.Success(new AiResponse { TextMessage = "Review the routine at 11 PM." });
            });
        var message = language == "pt-BR"
            ? "Toda noite às 21h30: ler 20 minutos, alongar por 10 minutos e apagar as luzes às 23h, como uma rotina da noite"
            : "Every evening at 9:30 PM: read 20 minutes, stretch for 10 minutes, and lights out at 11 PM, as one evening routine";
        var events = new List<ChatStreamEvent>();

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(_userId, message,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Id.Should().Be(original!.Id);
        pending.Items.Should().ContainSingle();
        rounds.Should().Be(sameTurn ? 2 : 0);
        pending.Items!.Single().Fields
            .Should().Contain(field => field.Field == "due_time" && field.ProposedValue!.Value.GetString() == "21:30");
        pending.PreviewFingerprint.Should().NotBe(original.PreviewFingerprint);
        var current = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        current.PreviewFingerprint.Should().Be(pending.PreviewFingerprint);
        current.OperationId.Should().Be(originalOperation);
        var routine = RoutineArguments(current.Arguments);
        routine.GetProperty("due_time").GetString().Should().Be("21:30");
        var children = routine.GetProperty("sub_habits");
        children.GetArrayLength().Should().Be(3);
        children[0].GetProperty("due_time").GetString().Should().Be("21:30");
        children[0].GetProperty("due_end_time").GetString().Should().Be("21:50");
        children[1].GetProperty("due_time").GetString().Should().Be("21:50");
        children[1].GetProperty("due_end_time").GetString().Should().Be("22:00");
        children[2].GetProperty("due_time").GetString().Should().Be("23:00");
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeEmpty();
        result.Value.Actions.Should().BeEmpty();
        events.Should().NotContain(item => item.Type == "delta");
        _productAnalytics.Received().CaptureUserEvent(_userId, "astra_answer_composed", Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, object>?>());
        _pendingOperationStore.GetAwaiting(_userId).Should().ContainSingle();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptyContinuation_CancelledPreviewTurnDoesNotRecover()
    {
        ScriptToolCall("""{"title":"Read","frequency_unit":"Day"}""");
        using var cancellation = new CancellationTokenSource();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Result.Failure<AiResponse>(ErrorMessages.AiEmptyResponse);
            });

        var act = () => CreateHandler().Handle(new(_userId, "Create a daily reading habit"), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static JsonElement RoutineArguments(JsonElement arguments) =>
        arguments.TryGetProperty("habits", out var habits) ? habits[0] : arguments;
}
