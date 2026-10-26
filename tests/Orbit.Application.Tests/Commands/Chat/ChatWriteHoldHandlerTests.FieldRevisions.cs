using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
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
    [InlineData("en", "due_time", "\"20:00\"", "Move it to evenings")]
    [InlineData("pt-BR", "due_time", "\"20:00\"", "Mude para a noite")]
    [InlineData("en", "frequency_unit", "\"Week\"", "Make it weekly")]
    [InlineData("pt-BR", "frequency_unit", "\"Week\"", "Faça semanalmente")]
    [InlineData("en", "end_date", "\"2026-12-31\"", "End it on December 31")]
    [InlineData("pt-BR", "end_date", "\"2026-12-31\"", "Termine em 31 de dezembro")]
    [InlineData("en", "reminder_times", "[30]", "Remind me half an hour before")]
    [InlineData("pt-BR", "reminder_times", "[30]", "Lembre meia hora antes")]
    [InlineData("en", "tag_names", "[\"Health\"]", "Tag it Health")]
    [InlineData("pt-BR", "tag_names", "[\"Saúde\"]", "Use a etiqueta Saúde")]
    [InlineData("en", "parent_habit_id", null, "Move it under Before Bed")]
    [InlineData("pt-BR", "parent_habit_id", null, "Coloque dentro de Before Bed")]
    public async Task ConversationalRevision_ChangesOnlyNamedField(string language, string field, string? value, string message)
    {
        var parents = ParentAccount(language);
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var arguments = JsonSerializer.SerializeToElement(new
        {
            parent_habit_id = parents[0].Id,
            title = "Read",
            description = "A quiet chapter",
            frequency_unit = "Day",
            frequency_quantity = 1,
            due_time = "09:00",
            reminder_enabled = true,
            reminder_times = new[] { 10 },
            checklist_items = new[] { new { text = "Choose a book", is_checked = false } }
        });
        ScriptToolCall(arguments.GetRawText(), "create_sub_habit");
        var first = await CreateHandler().Handle(new(_userId, "Create Read under Post Lunch"), default);
        first.IsSuccess.Should().BeTrue(first.Error);
        var pending = first.Value.PendingOperations.Should().ContainSingle().Which;
        var before = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var editValue = value is null ? JsonSerializer.SerializeToElement(parents[1].Id) : JsonDocument.Parse(value).RootElement.Clone();
        ScriptToolCall(JsonSerializer.Serialize(new
        {
            pending_operation_id = pending.Id,
            items = new[] { new { item_id = pending.Items!.Single().ItemId,
                edits_json = JsonSerializer.Serialize(new Dictionary<string, JsonElement> { [field] = editValue }) } }
        }), "revise_pending_changes");

        var result = await CreateHandler().Handle(new(_userId, message,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.PendingOperations.Should().ContainSingle().Which.Id.Should().Be(pending.Id);
        var after = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        after.OperationId.Should().Be(before.OperationId);
        JsonElement.DeepEquals(after.Arguments.GetProperty(field), editValue).Should().BeTrue();
        foreach (var property in before.Arguments.EnumerateObject().Where(property => property.Name != field))
            after.Arguments.GetProperty(property.Name).GetRawText().Should().Be(property.Value.GetRawText(), property.Name);
        after.Arguments.EnumerateObject().Select(property => property.Name).Should()
            .BeEquivalentTo(before.Arguments.EnumerateObject().Select(property => property.Name).Append(field).Distinct());
        _pendingOperationStore.IsAwaitingConfirmation(_userId, pending.Id).Should().BeTrue();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", false, "Is this a good idea?", "A small routine can make it easier to stay consistent. Keep it manageable.")]
    [InlineData("en", true, "Is this a good idea?", "A small routine can make it easier to stay consistent. Keep it manageable.")]
    [InlineData("pt-BR", false, "Você acha uma boa ideia?", "Uma rotina pequena pode ajudar a manter a consistência. Mantenha simples.")]
    [InlineData("pt-BR", true, "Você acha uma boa ideia?", "Uma rotina pequena pode ajudar a manter a consistência. Mantenha simples.")]
    public async Task PreviewOpinion_AnswersOneSentenceBesideUnchangedPendingPreview(string language, bool composition, string message, string answer)
    {
        _user.SetLanguage(language);
        if (composition)
            _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
                .Returns(new[] { FeatureFlagKeys.AstraBlockFirst });
        ScriptToolCall("""{"title":"Read","frequency_unit":"Day","due_time":"09:00"}""");
        var first = await CreateHandler().Handle(new(_userId, "Create a reading habit"), default);
        var pending = first.Value.PendingOperations.Should().ContainSingle().Which;
        var before = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        ScriptToolCall(JsonSerializer.Serialize(new { pending_operation_id = pending.Id, items = Array.Empty<object>() }),
            "revise_pending_changes");
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = answer }));
        var events = new List<ChatStreamEvent>();

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(_userId, message,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true),
            StreamSink: item => { events.Add(item); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().Be(answer[..(answer.IndexOf('.') + 1)]);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatTextPart>()
            .Which.Text.Should().Be(result.Value.AiMessage);
        var retained = result.Value.PendingOperations.Should().ContainSingle().Which;
        retained.Id.Should().Be(pending.Id);
        retained.PreviewFingerprint.Should().Be(pending.PreviewFingerprint);
        var after = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        after.Arguments.GetRawText().Should().Be(before.Arguments.GetRawText());
        after.ExpiresAtUtc.Should().Be(before.ExpiresAtUtc);
        _pendingOperationStore.IsAwaitingConfirmation(_userId, pending.Id).Should().BeTrue();
        result.Value.Actions.Should().BeEmpty();
        events.Should().Contain(item => item.Type == "delta");
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }
}
