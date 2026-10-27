using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("send_support_request", false)]
    [InlineData("revise_pending_changes", false)]
    [InlineData("create_habit", false)]
    [InlineData("create_habit", true)]
    public async Task FreshCreateRecovery_RejectsUnrelatedCallsInvalidCreationsAndRepeatedRevisions(string candidate, bool truncated)
    {
        ScriptToolCall("{\"title\":\"Read\",\"frequency_unit\":\"Day\"}");
        var first = await CreateHandler().Handle(new(_userId, "Create a reading habit"), default);
        var old = first.Value.PendingOperations.Should().ContainSingle().Which;
        var original = _pendingOperationStore.GetExecution(_userId, old.Id)!.Arguments.GetRawText();
        ScriptToolCall(JsonSerializer.Serialize(new
        {
            pending_operation_id = old.Id,
            items = new[] { new { item_id = old.Items!.Single().ItemId, edits_json = "{\"due_time\":\"invalid\"}" } }
        }), "revise_pending_changes");
        _aiIntentService.ClearReceivedCalls();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new AiResponse
            {
                IsTruncated = truncated,
                ToolCalls = [new(candidate, "replan", JsonSerializer.SerializeToElement(new { title = "New habit", frequency_unit = "Day", due_time = "invalid" }))]
            }));

        var result = await CreateHandler().Handle(new(_userId, "Crie um hábito chamado Ler todo dia",
            ClientContext: new(Locale: "pt-BR", SupportsParts: true, SupportsPendingOperationChanges: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().Be("Não consegui concluir isso. Tente novamente.");
        result.Value.PendingOperations.Should().BeEmpty();
        _pendingOperationStore.GetAwaiting(_userId).Should().ContainSingle().Which.PendingOperationId.Should().Be(old.Id);
        _pendingOperationStore.GetExecution(_userId, old.Id)!.Arguments.GetRawText().Should().Be(original);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }
}
